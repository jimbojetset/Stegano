using SkiaSharp;
using System.Text;

namespace Stegano;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] is "--help" or "-h")
        {
            Console.WriteLine("""
Steganography console
Usage: dotnet run --project Stegano.csproj -- [embed|extract]

Select input and output files using desktop dialogs. Cancel any dialog to stop.
Embedding saves a PNG and a separate extraction file. Keep both for extraction.
Passwords are optional. Stealth defaults to Maximum. Linux requires zenity.
""");
            return 0;
        }
        if (args.Length > 1)
        {
            Console.Error.WriteLine("Specify embed or extract, or --help.");
            return 1;
        }
        string? command = args.FirstOrDefault();
        if (command is null)
        {
            Console.Write("Embed or extract? [e/x]: ");
            command = Console.ReadLine();
            if (command is null)
                return 0;
        }
        try
        {
            switch (command.Trim().ToLowerInvariant())
            {
                case "e":
                case "embed": Embed(); break;
                case "x":
                case "extract": Extract(); break;
                default:
                    Console.Error.WriteLine("Specify embed or extract, or --help.");
                    return 1;
            }
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or SteganoException or InvalidOperationException or ArgumentException or PlatformNotSupportedException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static void Embed()
    {
        string? imagePath = FileDialogs.OpenFile("Select the source image");
        if (Cancelled(imagePath)) return;
        string? inputPath = FileDialogs.OpenFile("Select the file to hide");
        if (Cancelled(inputPath)) return;
        using SKBitmap source = SKBitmap.Decode(imagePath)
            ?? throw new InvalidDataException("The selected image could not be decoded.");
        string? password = ReadPassword();
        if (password is null) return;
        if (password.Length > 0)
        {
            string? confirmation = ReadPassword("Confirm password: ");
            if (confirmation is null) return;
            if (password != confirmation)
                throw new ArgumentException("Passwords do not match.");
        }
        Stegano.Stealthiness? stealth = ReadStealth();
        if (stealth is null) return;
        string name = Path.GetFileName(inputPath!);
        int capacity = Stegano.GetCapacity(source, name, stealth.Value);
        Console.WriteLine($"Available file capacity: {capacity:N0} bytes.");
        if (new FileInfo(inputPath!).Length > capacity)
            throw new InvalidOperationException("The file exceeds this image's capacity at the selected stealth setting.");

        string? outputPath = FileDialogs.SaveFile("Save the embedded image as PNG", "embedded.png");
        if (Cancelled(outputPath)) return;
        if (!string.Equals(Path.GetExtension(outputPath), ".png", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a filename ending in .png to preserve the embedded pixels.");
        string? extractionPath = FileDialogs.SaveFile("Save the separate extraction file", "extraction.bin");
        if (Cancelled(extractionPath)) return;
        RequireDifferentPaths(outputPath!, imagePath!, inputPath!);
        RequireDifferentPaths(extractionPath!, imagePath!, inputPath!, outputPath!);

        var result = Stegano.Embed(source, name, File.ReadAllBytes(inputPath!), password, stealth.Value);
        using SKBitmap bitmap = result.Bitmap;
        using SKData png = bitmap.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("Could not encode the output image as PNG.");
        File.WriteAllBytes(extractionPath!, result.ExtractionFile);
        Console.WriteLine($"Extraction file saved: {extractionPath}");
        using (FileStream output = File.Create(outputPath!))
            png.SaveTo(output);
        Console.WriteLine($"Image saved: {outputPath}");
    }

    private static void Extract()
    {
        string? imagePath = FileDialogs.OpenFile("Select the embedded image");
        if (Cancelled(imagePath)) return;
        string? extractionPath = FileDialogs.OpenFile("Select its extraction file");
        if (Cancelled(extractionPath)) return;
        string? password = ReadPassword();
        if (password is null) return;
        using SKBitmap image = SKBitmap.Decode(imagePath)
            ?? throw new InvalidDataException("The selected image could not be decoded.");
        EmbeddedFile file = Stegano.Extract(image, File.ReadAllBytes(extractionPath!), password);
        string name = Path.GetFileName(file.FileName.Replace('\\', '/'));
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
            name = "recovered.bin";
        string? outputPath = FileDialogs.SaveFile("Save the recovered file", name);
        if (Cancelled(outputPath)) return;
        RequireDifferentPaths(outputPath!, imagePath!, extractionPath!);
        File.WriteAllBytes(outputPath!, file.Contents);
        Console.WriteLine($"Recovered file saved: {outputPath}");
    }

    private static bool Cancelled(string? path)
    {
        if (path is not null) return false;
        Console.WriteLine("Cancelled.");
        return true;
    }

    private static void RequireDifferentPaths(string output, params string[] inputs)
    {
        StringComparison comparison = OperatingSystem.IsLinux()
            ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        foreach (string input in inputs)
            if (string.Equals(Path.GetFullPath(output), Path.GetFullPath(input), comparison))
                throw new ArgumentException("Choose separate output files without replacing an input file.");
    }

    private static string? ReadPassword(string prompt = "Password (Enter for none): ")
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected)
            return Console.ReadLine();
        var password = new StringBuilder();
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return password.ToString();
            }
            if (key.Key == ConsoleKey.Escape)
            {
                Console.WriteLine("\nCancelled.");
                return null;
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (password.Length > 0) password.Length--;
            }
            else if (!char.IsControl(key.KeyChar))
                password.Append(key.KeyChar);
        }
    }

    private static Stegano.Stealthiness? ReadStealth()
    {
        while (true)
        {
            Console.Write("Stealth [maximum/medium/minimum/none] (Enter for maximum): ");
            string? answer = Console.ReadLine();
            if (answer is null) return null;
            switch (answer.Trim().ToLowerInvariant())
            {
                case "":
                case "maximum": return Stegano.Stealthiness.Maximum;
                case "medium": return Stegano.Stealthiness.Medium;
                case "minimum": return Stegano.Stealthiness.Minimum;
                case "none": return Stegano.Stealthiness.None;
                default: Console.WriteLine("Choose maximum, medium, minimum, or none."); break;
            }
        }
    }
}
