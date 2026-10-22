# Contributing to MortisFlash

Thank you for your interest in MortisFlash. Bug reports, test cases and pull requests are welcome.

## Reporting a bug

Open an [issue](https://github.com/Nekuzaky/Mortis-flash-decompiler/issues/new/choose) and include:

- the MortisFlash version (shown in the file properties of the executable, or the release you downloaded);
- what you did, what you expected and what happened instead;
- for decompilation or compilation problems, the script path and the relevant part of the output (or the p-code).

Only attach SWF files you are allowed to share. When a file cannot be shared, a minimal script that reproduces the
problem is the most useful thing you can provide.

## Working on the code

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download) on Windows.
2. Build with `dotnet build MortisFlash.slnx` and run the app with `dotnet run --project src/MortisFlash`.
3. Keep the build free of warnings: warnings are treated as errors and the .NET analyzers run at the latest level.
4. Follow the style of the surrounding code and keep loops and recursion bounded.
5. After changing the decompilers, compilers or assemblers, run the self-test on a few files and compare the results
   before and after your change:

   ```bash
   dotnet run --project src/MortisFlash.Cli -c Release -- selftest path/to/file.swf
   ```

   It checks that the ABC bytecode is rewritten byte for byte and that scripts survive p-code and source round trips.

See [docs/architecture.md](docs/architecture.md) for an overview of the code and [docs/building.md](docs/building.md)
for publishing.

## Pull requests

- Keep each pull request focused on one change and describe what it fixes or adds.
- Mention the files you tested with and the self-test results when the change affects code generation.
- By submitting a pull request you agree that your contribution is licensed under the [MIT License](LICENSE).
