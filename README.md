**SolidarityGrid**

Prueba de Concepto (PoC) en .NET 8 diseñada como una malla (mesh) de nodos de procesamiento de pagos distribuida y autocurativa, capaz de operar sin punto único de fallo.
En esta arquitectura, los nodos colaboran vigilando la ejecución de sus pares. Si un nodo sufre una falla fatal a mitad de una transacción, los nodos supervivientes detectan el abandono y reclaman el procesamiento, garantizando idempotencia y alta disponibilidad.

**Arquitectura del Sistema**

El clúster está compuesto por 3 contenedores .NET 8 idénticos (`node-a`, `node-b`, `node-c`) y una base de datos **PostgreSQL** que actúa como estado compartido para la coordinación atómica mediante el patrón **Distributed Lease / Optimistic Locking**.


Client Request (POST /pay)
             │
             ▼
      ┌─────────────┐
      │   Node A    │ ─── Acquiéreme el Lease (TX-99, Node A, Expiration: +3s)
      └──────┬──────┘
             │ (Crash abrupto en t=3s)
             ✕
      ────────────────────────────────────────
      Monitores de Salud (Heartbeat / DB Check)
      ────────────────────────────────────────
             │
     ┌───────┴───────┐
     ▼               ▼
┌─────────┐     ┌─────────┐
│ Node B  │     │ Node C  │ ─── Detectan Lease expirado de TX-99
└────┬────┘     └─────────┘
     │
     └─── Tries Atomic Claim ───► [Node B se adueña del Lock] ───► Procesa y Completa

**Estructura del Proyecto**

SolidarityGrid/
├── docker-compose.yaml
├── run-simulation.sh
└── src/
    └── SolidarityGrid.Node/
        ├── SolidarityGrid.Node.csproj
        ├── Program.cs
        ├── Models.cs
        ├── AppDbContext.cs
        ├── TransactionProcessor.cs
        ├── HealthMonitorService.cs
        ├── appsettings.json
        └── Dockerfile

**Preguntas y Justificación Técnica** 

1. ¿Cómo detectan los nodos que un compañero ha caído?

A través de un mecanismo de Leases con Timeout. 
Cada nodo actualiza periódicamente el timestamp de procesamiento de la transacción que tiene asignada. Los nodos supervivientes ejecutan un servicio en segundo plano (HealthMonitorService) que escanea periódicamente las transacciones activas. Si el timestamp de un nodo no se ha actualizado dentro del umbral de tiempo tolerado (ej. 3 segundos), el sistema asume que el nodo sufrió una falla fatal.

*¿Cómo garantiza esto que solo uno gane?*

1. Carrera de Condición: Ambos nodos envían la consulta casi en el mismo milisegundo.
2. Aislamiento en PostgreSQL: La base de datos bloquea la fila por un microsegundo y atiende primero una de las dos peticiones (ejemplo: Nodo B).
3. El Ganador (Nodo B): Como la condición LeaseExpiresAt < NOW() se cumple, PostgreSQL actualiza la fila, le cambia el dueño a Node-B, renueva el tiempo y devuelve 1 fila afectada. El Nodo B procede a finalizar el pago.
4. El Perdedor (Nodo C): Cuando la consulta del Nodo C entra un microsegundo después, la fila ya fue modificada por el Nodo B, por lo que LeaseExpiresAt ya no está vencido. La condición falla y PostgreSQL devuelve 0 filas afectadas. El Nodo C detecta que perdió la carrera y aborta inmediatamente sin hacer nada.


2. ¿Cómo evitas que el Nodo B y el Nodo C procesen el mismo pago dos veces (Double Spending)?

Mediante Optimistic Locking / Compare-And-Swap (CAS) en la base de datos PostgreSQL.
Cuando los nodos B y C detectan que A cayó, ambos intentan reclamar la transacción ejecutando una sentencia atómica UPDATE. La consulta incluye una condición estricta sobre el estado actual y el número de versión/lease expirado. La base de datos garantiza aislamiento y atomicidad haciendo que el primer nodo en llegar actualiza la fila y gana el derecho exclusivo de procesamiento; el segundo nodo recibe 0 filas afectadas y aborta la operación de inmediato.

                     [TX-99 Detectada como Huérfana]
                                    |
          +-------------------------+-------------------------+
          |                                                   |
          v                                                   v
   [Nodo B intenta Reclamar]                           [Nodo C intenta Reclamar]
          |                                                   |
          +-------------------------+-------------------------+
                                    |
                                    v
                     +-----------------------------+
                     |    PostgreSQL Engine        |
                     | (Ejecuta Bloqueo de Fila)   |
                     +--------------+--------------+
                                    |
            +-----------------------+-----------------------+
            | Gana por microsegundos                        | Llega un instante después
            v                                               v
  +-------------------+                           +-------------------+
  |      NODO B       |                           |      NODO C       |
  | Filas Afectadas: 1|                           | Filas Afectadas: 0|
  +---------+---------+                           +---------+---------+
            |                                               |
            v                                               v
[ Procesa y Marca]                                   [Aborta / Ignora]
  [ Status = Completed ]                           [  No hace nada    ]


3. ¿Por qué este diseño es elegido?

Por simplicidad y eliminación de puntos únicos de fallo externos. 
Agregar herramientas como RabbitMQ o Redis Pub/Sub trasladaría el problema del SPOF hacia el broker. Esta solución descentralizada resuelve el consenso directamente en el código de la aplicación usando PostgreSQL como punto de coordinación ACID, reduciendo la complejidad operacional y la huella de infraestructura.


**Despliegue en Un Solo Paso**

Todo el entorno se configura y despliega automáticamente sin intervención humana.

Desde la raíz del proyecto, ejecuta:

*docker-compose up*

Esto levantará PostgreSQL, creará automáticamente el esquema de datos y pondrá en marcha los 3 nodos de la red (node-a, node-b, node-c).

En otro apartado puedes hacer manualmente el enviar el pago al Nodo A con:

$body = @{ amount = 150.50 } | ConvertTo-Json
# Enviar pago al Nodo A
Invoke-RestMethod -Uri http://localhost:8081/pay -Method Post -Body $body -ContentType "application/json"

luego terminar el proceso del Nodo A con:

Mdocker stop node-a

**Uso del Script**

Ejecutando el Script usando Git puedes ejecutarlo desde su carpeta raíz 

De forma manual puedes usar el Git-Bash del terminal con el comando:

*./run-simulation.sh*  


**¿Qué ejecuta la simulación?**

1. Limpia y Levanta: Reinicia los contenedores y la base de datos desde cero (docker-compose down -v + up).

2. Carga de Trabajo: Envía una solicitud de pago de $250.00 al Nodo A (http://localhost:8081/pay).

3. Inyección de Falla (Chaos Test): A los 3 segundos de iniciado el procesamiento, detiene abruptamente el contenedor de Nodo-A (docker stop node-a).

4. Monitoreo y Autocuración: Un nodo superviviente (Nodo-B o Nodo-C) detecta la pérdida de comunicación, asume la transacción y la completa.

5. Verificación: Muestra los logs del evento y confirma que la transacción quedó en estado Completed.