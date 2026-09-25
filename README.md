# Stegano

Stegano hides a file in the low bits of an image's RGB pixels. It runs in a console and uses desktop dialogs to open and save files.

Embedding creates a PNG and a separate extraction file. You need both to recover the hidden file, along with the password if you used one.

## Requirements

- .NET 10 SDK
- A desktop session on Windows, macOS, or Linux
- Windows: Windows PowerShell
- Linux: `zenity`

## Build and run

From the project directory:

```sh
dotnet build
dotnet run -- embed
dotnet run -- extract
```

Run `dotnet run` to choose an operation interactively, or `dotnet run -- --help` for help.

## Hide a file

1. Choose the source image.
2. Choose a stealth setting. The console shows the maximum file size in bytes.
3. Choose the file to hide. If it is too large, the app asks you to choose another.
4. Enter a password, or press Enter to leave the contents unencrypted. Confirm the password if you entered one.
5. Choose separate destinations for the PNG and extraction file.

The source image must be fully opaque and decode to 8-bit RGBA or BGRA pixels. The filename, including its extension, must fit in 255 UTF-8 bytes. The capacity shown always reserves those 255 bytes, even for a shorter filename.

## Recover a file

1. Choose the embedded image and its extraction file.
2. Enter the original password, or press Enter if none was used.
3. Choose where to save the recovered file.

Cancel any file dialog to stop. No output is written until all destination dialogs have been accepted.

## Stealth settings

`maximum` is the default. It skips image borders and areas where a pixel and its four neighbours have nearly the same colour. `medium` and `minimum` accept progressively smaller colour differences. `none` uses all pixels.

Extraction reads the setting from the extraction file. You do not have to remember it.

## Encryption and image handling

Password-protected files use AES-GCM. Authentication fails if the password is wrong or the embedded data or extraction file has been altered. Unencrypted files use a SHA-256 checksum to detect corruption.

The extraction file holds the lengths, stealth setting, encryption parameters, and authentication tag or checksum. It contains no password or encryption key. No format header is written into the image.

Keep the output PNG unchanged. Resizing, editing, or converting it to JPEG can destroy the hidden data. Statistical analysis may still detect embedding, including at maximum stealth.
