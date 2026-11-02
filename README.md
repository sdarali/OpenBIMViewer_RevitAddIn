# Open BIM Viewer for Revit

A Revit add-in for reviewing and raising BCF issues on an [IFC Collab](../IFC_Collab_Server)
server, from a dockable panel. It talks to the server through the shared
[IFC Collab Core](../IFCCollabCore), the same C++ business logic the VR and Android apps use,
so sign-in, token refresh, offline queueing and the wire format behave the same everywhere.

Builds for **Revit 2021 to 2027** from one code base.

## What it does

Ribbon tab **IFC Collab → Issues & Views** opens the **Open BIM Viewer** panel (docked behind the
Project Browser the first time).

- **Sign in** with your IFC Collab e-mail and password. Only an encrypted session is kept (Windows
  DPAPI, current Windows user); the password is never stored. The server box offers the last 5
  servers you signed in to. If Windows doesn't trust the server's HTTPS certificate (a self-signed
  development server, say), the panel shows the certificate and its SHA-256 fingerprint and asks
  whether to trust that exact certificate for that server.
- **Issues**: list, search and filter by status. Select one to edit its **title, status, type,
  priority, assignee and description** (Save changes sends only what you changed), see its image
  and its **comments**, and **post comments**, optionally with a screenshot and the camera of the
  active 3D view (and marked up first).
- **New issue…** takes a screenshot and the camera of the active 3D view, lets you mark it up or
  retake it, and links the selected elements by IFC GlobalId.
- **Saved views**: the project's saved views with their previews.
- **Images** (issue, comment and saved-view):
  - **Double-click** an image that has a camera to show that view in Revit.
  - **View** opens it full size.
  - For issue and comment images: **Mark up** (pen, highlighter, line, arrow, box, ellipse,
    text; colours, widths, undo/redo), **Replace with current view**, or **Replace from file**.
    The viewpoint's camera stays as it was; only the image changes.
- **Showing a view in Revit** always uses one 3D view, named **`Open-BIM-View`**: it is created the
  first time and reused after that, so opening viewpoint after viewpoint never adds views to the
  project. It is a perspective view without a far clip plane.
- **Offline**: when the server can't be reached, the issue and view lists come from the cache, and
  new issues, comments and issue edits are queued (the dot next to your name turns orange). **Sync
  now** sends them; refused changes stay listed with the server's reason.

## Repository layout

```
native/IfcCollabCoreBridge/      IfcCollabCore.dll: the IFC Collab Core (compiled from ../IFCCollabCore)
                                 behind a flat C interface (collab_bridge.h). x64, static CRT, one DLL
                                 for every Revit version.
src/OpenBimViewer.Collab/        netstandard2.0. The Core behind a Task API (CollabClient), with HTTP
                                 (HttpClient), certificate pinning, DPAPI, IFC GlobalIds and a small JSON
                                 reader/writer. No Revit dependency and no NuGet dependencies.
src/OpenBimViewer.Revit/         The add-in: ribbon, dockable WPF panel, dialogs, Revit viewpoints.
tests/OpenBimViewer.Collab.Tests/ The managed client and the native Core together, against a scripted
                                 server. Runs on .NET 10 and .NET Framework 4.8.
build.ps1, install.ps1           Build everything / install for the current Windows user.
```

### How the pieces fit

```
WPF panel ──await──▶ CollabClient (C#) ──P/Invoke──▶ IfcCollabCore.dll (C++ Core: rules, JSON, offline)
                          ▲                                  │
                          └──── host callbacks: HTTP, timers, session ◀┘
```

The Core does the business rules and the wire format; the C# side only gives it HTTP, timers and
encrypted session storage. Everything runs on Revit's UI thread, which is the Core's "logic
thread". Revit API work (screenshots, cameras, the 3D view) is queued to an `ExternalEvent`
(`RevitTasks`), because a modeless panel can't call the Revit API directly.

## Building for several Revit versions

Each Revit version needs its own .NET:

| Revit       | .NET                 |
|-------------|----------------------|
| 2021 – 2024 | .NET Framework 4.8   |
| 2025 – 2026 | .NET 8               |
| 2027        | .NET 10              |

The add-in project is built once per version with `-p:RevitVersion=<year>`. That property picks the
target framework, the Revit API reference assemblies (Autodesk's, from the
`Nice3point.Revit.Api.*` NuGet packages, so no Revit install is needed to build), and defines
`REVIT<year>` and `REVIT<year>_OR_GREATER` symbols for version-specific code. Intermediate files are
kept per version under `build\obj\`, so versions can be built one after another without cleaning.

Requirements:
- .NET SDK 10 (it builds every target, including .NET Framework 4.8)
- Visual Studio 2022 or 2026 with **Desktop development with C++**, for `IfcCollabCore.dll`
- The IFC Collab Core checked out beside this folder (`..\IFCCollabCore`), or pass `-CollabCoreDir`

```powershell
.\build.ps1                          # native DLL, tests, and Revit 2021-2027
.\build.ps1 -Versions 2026           # just Revit 2026
.\build.ps1 -Versions 2024,2027 -SkipTests
dotnet build src\OpenBimViewer.Revit -p:RevitVersion=2025    # the add-in alone (native DLL already built)
```

Output: `build\dist\<year>\OpenBimViewer.Revit.addin` and `build\dist\<year>\OpenBimViewer\`.

The native project uses Visual Studio 2026's toolset (v145); for Visual Studio 2022 build it with
`msbuild native\IfcCollabCoreBridge\IfcCollabCoreBridge.vcxproj -p:Configuration=Release -p:Platform=x64 -p:PlatformToolset=v143`.

## Install

```powershell
.\install.ps1                  # every built version whose Revit is installed
.\install.ps1 -Versions 2026
.\install.ps1 -Uninstall
```

This copies the manifest and the `OpenBimViewer` folder to
`%APPDATA%\Autodesk\Revit\Addins\<year>\`. Close Revit first; it locks the DLLs. To debug, build
Debug (`.\build.ps1 -Configuration Debug -Versions 2026`), install, start Revit and attach to
`Revit.exe`.

## Tests

```powershell
dotnet test tests\OpenBimViewer.Collab.Tests
```

They exercise the real `IfcCollabCore.dll` through the managed client: sign-in and session resume,
the 401 refresh-and-replay, issues and views with their snapshot URLs and cameras, creating and
updating issues (only changed fields are sent), comments with views, replacing snapshots, binary
downloads, the offline queue and sync, plus JSON, IFC GlobalIds and DPAPI. They run on .NET 10 and
.NET Framework 4.8. The Core's own C++ tests live in `..\IFCCollabCore` (`Scripts\run-tests.bat`).

## Configuration

`%APPDATA%\IfcCollab\Revit\settings.json`:

- **`coordinateBase`**: where the IFC files' coordinates sit in the Revit model: `Shared`
  (default; Revit's IFC export default), `Internal` or `ProjectBasePoint`. It must match the
  *Coordinate Base* the model was exported to IFC with, or cameras land in the wrong place.
- **`viewName`**: the reused 3D view (default `Open-BIM-View`).
- **`fieldOfView`**: the vertical field of view sent with captured views (default 60°). Revit's API
  doesn't expose a camera's field of view, so a value is assumed.
- **`snapshotWidth`**: screenshot width in pixels (default 1600).
- **`trustedCertificates`**: `"host:port"` → pinned SHA-256 fingerprints. Delete an entry to stop
  trusting it.

Also in `%APPDATA%\IfcCollab\Revit\`: `session.bin` (the DPAPI-encrypted session). In
`%LOCALAPPDATA%\IfcCollab\Revit\`: `cache\` (the Core's offline cache and outbox) and `addin.log`.

## Coordinates

BCF cameras are in the IFC file's coordinates (metres, Z up). The add-in converts them to and from
Revit's internal coordinates (feet) through the chosen `coordinateBase`. For `Shared`, it uses the
active project location's transform and checks its direction against the project base point's
internal and shared positions. Isometric views are captured as a perspective camera standing far
enough back to see what the view shows.

## Known limitations

- **Not yet exercised inside Revit.** Everything builds for 2021 to 2027, the tests pass, and the
  panel and dialogs were loaded and rendered outside Revit. The Revit-side parts (ribbon, dock pane,
  `ExternalEvent`, `ExportImage`, cameras, `Open-BIM-View`) still need a hands-on pass, first in
  2024 and 2027 (installed here) and then in 2026.
- **Field of view** is assumed (see `fieldOfView`) rather than read from or applied to Revit's camera.
- Showing a view applies the **camera only**: the Core reduces issues to their camera, image and
  pin, so selection, hidden elements, colours and section planes aren't applied. New issues do send
  the selected elements.
- **Replacing an image needs the server**: the Core queues issues, comments and edits offline, but
  not image replacements.
- **Assignee** is typed as an e-mail; the server checks it's a project member.
- **Microsoft-SSO-only accounts** can't sign in (the Core's login is e-mail and password; its
  pairing-code sign-in isn't wired up here yet).
