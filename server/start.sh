#!/bin/bash
set -e
cd "$(dirname "$0")"
echo "=========================================="
echo "  AutoDiag AI Server v1.0.16"
echo "  Starting..."
echo "=========================================="
if [ -z "$PORT" ]; then export PORT=8000; fi
for var in DEEPSEEK_API_KEY LICENSE_SECRET; do
    if [ -z "${!var}" ]; then echo "WARNING: $var is not set!"; fi
done
mkdir -p /data
exec gunicorn main:app -w 2 -k uvicorn.workers.UvicornWorker --bind 0.0.0.0:$PORT --access-logfile - --error-logfile - --capture-output --enable-stdio-inheritance
