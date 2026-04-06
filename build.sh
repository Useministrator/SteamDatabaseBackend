#!/bin/bash

echo Building Steam Database...

cd "$(dirname "$0")"

rm -rf bin/ obj/

dotnet publish --configuration Release --framework net10.0 -p:PublishSingleFile=true -p:NuGetLockFilePath=obj/publish.packages.lock.json --runtime linux-x64 --self-contained true
