#!/usr/bin/env bash
# Gera um executável único e autônomo para Linux em ./publish
set -euo pipefail
dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o publish
echo "Pronto: publish/resetexplode"
