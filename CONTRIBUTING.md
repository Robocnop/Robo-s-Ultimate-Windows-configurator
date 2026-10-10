# Contributing to Robo's Ultimate Windows Configurator

Thanks for taking the time to help! Bug reports, ideas, translations and pull requests are all welcome.
Issues can be written in **English or French**.

- [Code of conduct](#code-of-conduct)
- [Reporting a bug](#reporting-a-bug)
- [Suggesting a feature](#suggesting-a-feature)
- [Setting up the project](#setting-up-the-project)
- [Making a change](#making-a-change)
- [Pull requests](#pull-requests)

## Code of conduct

Everyone taking part in this project is expected to follow the [code of conduct](CODE_OF_CONDUCT.md).

## Reporting a bug

Open an [issue](https://github.com/Robocnop/Robo-s-Ultimate-Windows-configurator/issues) with:

- your Windows version (for example *Windows 11 Pro 24H2*) and the app version (shown in the exe properties);
- the step that went wrong and what you expected instead;
- the log: `C:\RoboConfigurator\log.txt` (or `journal.txt` on a French Windows). For an Edge problem, also attach
  `edge-uninstall.log` (or `edge-desinstallation.log`) from the same folder.

> [!IMPORTANT]
> Remove anything private before posting: Windows product key, serial number, MAC and IP addresses. The PC
> report contains some of them, and the log can show the Windows key when one was installed.

If the working folder was already cleaned up, run the app again with **Clean up when done** turned off.

## Suggesting a feature

Open an issue describing the problem you want to solve, not only the solution. The app is meant to stay a
single portable exe that runs with what ships with Windows: suggestions that need extra runtimes or an
installer are unlikely to be accepted.

## Setting up the project

Requirements: Windows 10 or 11 and the [.NET SDK](https://dotnet.microsoft.com/download) (8 or later).
`build.ps1` installs a local copy of the SDK if none is found.

```powershell
git clone https://github.com/Robocnop/Robo-s-Ultimate-Windows-configurator.git
cd Robo-s-Ultimate-Windows-configurator
powershell -ExecutionPolicy Bypass -File build.ps1        # Release build -> dist\RoboConfigurator.exe
dotnet build src\RoboConfigurator -c Debug                # Debug build, no administrator prompt
```

Try the interface without touching your PC with the Debug build and `-Simulation`:

```powershell
src\RoboConfigurator\bin\Debug\net472\RoboConfigurator.exe -Simulation -Lang en
src\RoboConfigurator\bin\Debug\net472\RoboConfigurator.exe -Simulation -Lang fr
```

Project layout:

| Path | Content |
|---|---|
| `src/RoboConfigurator/MainWindow.xaml(.cs)` | Interface: settings screen and progress screen |
| `src/RoboConfigurator/Core/Engine.cs` | Steps, renaming, report and clean-up, run in order without interaction |
| `src/RoboConfigurator/Core/Lang.cs` | Language selection and the `{local:T}` XAML extension |
| `src/RoboConfigurator/Core/Report.cs` | PC report |
| `src/RoboConfigurator/Core/Payloads.cs` | Download links of Ninite, CrapFixer, Office... |
| `brag-output/` | Demo videos and the page they are rendered from |

## Making a change

- **Both languages, always.** Every text the user can see (interface, log, report, error messages) exists in
  English and French: `Lang.T("English", "Français")` in C#, `{local:T 'English', 'Français'}` in XAML. Use a
  typographic apostrophe (’) inside XAML texts.
- **Never block the run.** Once started, the setup must go to the end on its own: catch errors per step, log
  them, and let the next step run.
- **Test in simulation** in both languages, and on a virtual machine for anything that changes the system.
- **Match the surrounding code**: same naming, same comment style, small focused changes.
- **Commit messages in English**, short subject line in the imperative (*Add...*, *Fix...*).

## Pull requests

1. Fork the repository and create a branch from `main`.
2. Make your change, then check that the Release build and the simulation work in English and French.
3. Open a pull request explaining what changes and why. Screenshots are welcome for interface changes.
4. The [CI](https://github.com/Robocnop/Robo-s-Ultimate-Windows-configurator/actions) builds the exe, checks its
   version and runs a smoke test: it must be green before merging.

Releases are made by the maintainer: bump `<Version>` in `RoboConfigurator.csproj`, then push a `vX.Y.Z` tag; the
CI builds the exe and attaches it to the release.
