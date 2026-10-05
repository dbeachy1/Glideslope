# Signed Windows builds

The 2.8.1 Windows installer uses Microsoft Azure Artifact Signing. The build script supports
the same flow when all three external inputs are available: Windows SDK `signtool.exe`, the matching x64
`Azure.CodeSigning.Dlib.dll`, and a metadata JSON file configured for the workstation's authenticated provider.
Keep the Dlib package and metadata outside the repository. The public repository must contain placeholders only;
never copy real tenant, account, endpoint, profile, or credential values into this file or tracked build settings.

From the repository root, use paths from your local signing setup:

```powershell
.\packaging\windows\build-installer.ps1 `
  -SignToolPath '<Windows SDK>\bin\<version>\x64\signtool.exe' `
  -DlibPath '<Azure Artifact Signing package>\x64\Azure.CodeSigning.Dlib.dll' `
  -MetadataPath '<private signing configuration>\metadata.json'
```

All three parameters are required together. With none supplied, the script makes an unsigned local build. Signed
mode checks that the inputs exist before publishing, signs the Glideslope-owned `Glideslope.App.exe`, and verifies
it with SignTool's Authenticode policy checks (`/pa /all /tw`). Neighboring dependency DLLs are not modified.
Inno Setup signs the Setup program and signed uninstaller using SHA-256 and the RFC 3161 timestamp service at
`http://timestamp.acs.microsoft.com`. It stores its temporary signed uninstaller in the build's owned temporary
directory. The script verifies Setup and that uninstaller before creating the package hash and publish manifest;
any signing or verification failure stops the build.
