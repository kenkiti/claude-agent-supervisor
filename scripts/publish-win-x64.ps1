$ErrorActionPreference = 'Stop'
dotnet publish .\src\AgentSupervisor.App\AgentSupervisor.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
