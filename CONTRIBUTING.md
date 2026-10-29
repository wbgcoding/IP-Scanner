# Contributing

## Before you start

- **Bugs and ideas:** open an issue with the matching template first, so we can agree on the
  approach before you spend time on code.
- **Security problems:** never in a public issue, see [SECURITY.md](SECURITY.md).

## Build and test

You need Windows 10 or 11 and the .NET 8 SDK.

```bat
dotnet build IP-Scanner.slnx
dotnet test IP-Scanner.slnx
```

`build.bat` builds the self-contained release file `IP-Scanner.exe` into `dist\`. The command line is the same exe run with arguments.

## Guidelines

- **Only what the user asked for.** Probes go to the targets the user entered, nothing else.
  The UI-free logic lives in `src/IpScanner.Core`, the WPF app only shows it.
- **Tests with every change.** New behaviour comes with a test; a bug fix comes with a test that
  failed before the fix.
- **Every language.** A new UI string goes into `Loc.cs` and gets the same key in every file under
  `Resources/lang`.
- **Both themes.** A new color gets the same key in `Theme.Dark.xaml` and `Theme.Light.xaml`.
- **One topic per pull request,** with a short description of what changed and how you tested it.
  Screenshots help for anything visible, in both the light and the dark theme.

## License

By contributing you agree that your contribution is licensed under the [MIT License](LICENSE).
