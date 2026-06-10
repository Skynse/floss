# Floss

Desktop drawing application built with Avalonia and C# on .NET 10.

## Build

```sh
dotnet restore
dotnet build
dotnet run --project src/Floss.App
```

## Publish

Windows builds require the **Visual C++ Redistributable for Visual Studio 2015–2022**. If you get "side-by-side configuration is incorrect", install [vc_redist.x64.exe](https://aka.ms/vs/17/release/vc_redist.x64.exe).

```sh
dotnet publish src/Floss.App/Floss.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false -o artifacts/floss-win-x64-compact
```

```sh
dotnet publish src/Floss.App/Floss.App.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false -o artifacts/floss-linux-x64-compact
```
