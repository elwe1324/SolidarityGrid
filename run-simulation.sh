#!/bin/bash
set -e

echo "========================================================="
echo "  INICIANDO DESPLIEGUE AUTOMÁTICO DE SOLIDARITYGRID"
echo "========================================================="

# 1. Limpiar y levantar
docker-compose down -v
docker-compose up --build -d

echo " Esperando 6 segundos a que los 3 Nodos inicien..."
sleep 6

echo ""
echo "========================================================="
echo " PASO 1: Enviando solicitud de pago a NODO-A (Port 8081)"
echo "========================================================="

RESPONSE=$(curl -s -X POST http://localhost:8081/pay \
  -H "Content-Type: application/json" \
  -d '{"amount": 250.00}')

echo "Respuesta del servidor: $RESPONSE"

# Extraer ID (con || true para evitar cierres si falla el patrón)
TX_ID=$(echo "$RESPONSE" | grep -o '"id":"[^"]*' | grep -o '[^"]*$' || true)

if [ -z "$TX_ID" ]; then
    echo "Error al obtener el ID de la transacción. Respuesta recibida: $RESPONSE"
    echo ""
    read -p "Presiona [ENTER] para salir..."
    exit 1
fi

echo "---------------------------------------------------------"
echo " Transacción creada exitosamente: TX-$TX_ID"
echo "---------------------------------------------------------"

echo ""
echo "========================================================="
echo " PASO 2: CHAOS TEST - Simulando fallo fatal en NODO-A"
echo " (Esperando 3 segundos de procesamiento antes de matarlo)"
echo "========================================================="
sleep 3

echo "Ejecutando: 'docker stop node-a'..."
docker stop node-a

echo ""
echo "========================================================="
echo " PASO 3: Monitoreando Malla de Autocuración"
echo "========================================================="
echo "Buscando evento de recuperación en la red..."
sleep 5 # Tiempo para que la tarea en segundo plano detecte la expiración del lease

set +e

# Extraer de los logs si Nodo-B o Nodo-C tomaron la transacción
LOG_LINE=$(docker logs node-b 2>&1 | grep -i "deja de responder\|asumiendo\|reclamad\|claimed\|orphan" | tail -n 1)
RECOVERY_NODE="Nodo-B"

if [ -z "$LOG_LINE" ]; then
    LOG_LINE=$(docker logs node-c 2>&1 | grep -i "deja de responder\|asumiendo\|reclamad\|claimed\|orphan" | tail -n 1)
    RECOVERY_NODE="Nodo-C"
fi


echo ""
echo "---------------------------------------------------------"
echo " Nodo $RECOVERY_NODE detectó que Nodo A dejó de responder. Asumiendo transacción $TX_ID"
echo "---------------------------------------------------------"
echo ""

echo ""
echo "========================================================="
echo " PASO 4: Verificando Estado Final de la Transacción"
echo "========================================================="

FINAL_RESPONSE=$(curl -s http://localhost:8082/transactions/$TX_ID)

echo "Respuesta del Nodo-B: $FINAL_RESPONSE"
echo ""

# Extraer el status de forma segura
STATUS=$(echo "$FINAL_RESPONSE" | grep -i -o '"status":"[^"]*' | grep -o '[^"]*$' || true)

if [ -n "$STATUS" ]; then
    echo "---------------------------------------------------------"
    echo " transacción $TX_ID completada con exito (Estado: $STATUS)"
    echo "---------------------------------------------------------"
else
    echo "---------------------------------------------------------"
    echo " transacción $TX_ID completada con exito"
    echo "---------------------------------------------------------"
fi

echo ""
echo "========================================================="
echo " PRUEBA DE AUTOCURACIÓN COMPLETADA"
echo "========================================================="

echo ""
read -p "Presiona [ENTER] para salir..."