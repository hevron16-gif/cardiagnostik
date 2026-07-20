#!/usr/bin/env bash
set -e
echo "=== CarDiagnostik API on 0.0.0.0:${PORT:-8000} ==="
exec uvicorn main:app --host 0.0.0.0 --port "${PORT:-8000}"
