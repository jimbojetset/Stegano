# Stegano

A .NET console app that hides a file inside an image using the low bits of its RGB pixels. Native file dialogs handle file selection and save locations.

Embedding produces two files: a PNG image and a separate extraction file. Keep both to recover the hidden file. If you use a password, you will also need that password.

## Requirements

- .NET 10 SDK
- A desktop session on Windows, macOS, or Linux
- Windows: Windows PowerShell
- Linux: `zenity` installed

## Build and run

Run these commands from the project directory:

```sh
dotnet build
dotnet run -- embed
dotnet run -- extract
```

Use `dotnet run` for an interactive choice, or `dotnet run -- --help` for help.

## Hide a file

1. Run `dotnet run -- embed`.
2. Choose the source image and the file to hide.
3. Enter an optional password and choose a stealth setting.
4. Choose where to save the PNG and the separate extraction file.

The source image must be fully opaque and decode to 8-bit RGBA or BGRA pixels. The app reports the available file capacity before embedding. The source files are left unchanged.

## Recover a file

1. Run `dotnet run -- extract`.
2. Choose the embedded image and its extraction file.
3. Enter the password used during embedding, or press Enter if none was used.
4. Choose where to save the recovered file.

Cancel any file dialog to stop the operation.

## Stealth and verification

`maximum` is the default. `medium` and `minimum` allow progressively more pixels; `none` uses all pixels. Enabled stealth skips nearly uniform areas and image borders. Extraction reads the setting automatically from the extraction file.

Password-protected payloads use AES-GCM authentication. Unencrypted payloads use a SHA-256 checksum to detect corruption. Neither the image nor the extraction file stores the password or encryption key.

Keep the output PNG unchanged. Resizing, image editing, or conversion to JPEG can destroy the hidden data. Stealth reduces changes in smooth areas; it does not guarantee resistance to statistical detection.
