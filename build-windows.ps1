$ErrorActionPreference = "Stop"
dotnet publish src/ServiceNowDesk.App/ServiceNowDesk.App.csproj -c Release -r win-x64 --self-contained true -o dist/ServiceNowDesk
Write-Host "Published to dist\ServiceNowDesk\ServiceNowDesk.exe"
