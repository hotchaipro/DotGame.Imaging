# Contributing

Thanks for helping with DotGame.Imaging. Bug reports, fixes and new tests are all welcome.

## Reporting a bug

Open an issue with the image that fails, if you can share it, and what you expected: the pixels another
decoder gives, or the file another encoder writes. For a security problem, see `SECURITY.md` instead.

## Making a change

Build and test with the .NET 11 SDK or later:

```bash
dotnet build
dotnet test
```

The build treats every warning as an error, including the code style in `.editorconfig`, so a change
that builds cleanly already follows it. `dotnet format` fixes most style errors.

A few things the library holds to:

- **The ports match their originals exactly.** The JPEG decoder gives libjpeg-turbo 3.2.1's pixels, and
  the WebP decoder and encoder give libwebp 1.6.0's pixels and cwebp 1.6.0's files, bit for bit. A change
  to them keeps that, and says how it was checked.
- **No dependencies, and trim and AOT safe.** The library references nothing beyond .NET.
- **Each fix comes with a test** that fails without it. Test images go in `test/Images/<format>/`, and
  damaged images that once broke a decoder in `test/Images/Fuzz/<format>/`.
- **Public API changes are discussed first,** in an issue, before a pull request. `eng/api/DotGame.Imaging/PublicAPI.Shipped.txt`
  and `eng/api/DotGame.Imaging/PublicAPI.Unshipped.txt` list the public API, and the build fails until a change to it is in
  `Unshipped`: the analyzer's code fix (or `dotnet format analyzers --diagnostics RS0016`) adds it.
- **The decoders take untrusted files.** A change to one is fuzzed with `fuzz/` (see its README)
  before it ships.
- **The docs follow the code.** `docs/api` is generated from the doc comments, and every public type
  and member has one: after changing a public API or its comments, run
  `dotnet run tools/api-docs/generate.cs` (`-- --check` fails if `docs/api` is out of date, and
  `-- --missing` lists members with no comment). After changing `README.md` or `docs/`,
  `python3 tools/api-docs/check-docs.py` checks their links and compiles the README's samples. CI
  runs both.

Open a pull request against `main`. By contributing, you agree that your contribution is licensed under
the MIT License, as the rest of the library is (`LICENSE`).
