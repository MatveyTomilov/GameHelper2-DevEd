using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using GameOffsets;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: SignatureCheck <client-executable> [candidate-patterns.json]");
    return 2;
}

try
{
    var file = args[0];
    var patterns = StaticOffsetsPatterns.Patterns.ToDictionary(pattern => pattern.Name, StringComparer.Ordinal);
    if (args.Length == 2)
    {
        var candidates = JsonSerializer.Deserialize<SignatureCandidate[]>(File.ReadAllText(args[1]),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (candidates is null || candidates.Length == 0 || candidates.Length > patterns.Count)
        {
            throw new InvalidDataException("Expected a nonempty array of candidates for the configured signatures.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (candidate?.Name is not { } name || !patterns.ContainsKey(name) || !names.Add(name) ||
                candidate.Pattern is not { Length: > 0 and <= 768 } mask || mask.Count(ch => ch == '^') != 1)
            {
                throw new InvalidDataException("Each candidate must name a different configured signature and contain one displacement marker.");
            }

            patterns[name] = new Pattern(name, mask);
        }
    }

    var image = File.ReadAllBytes(file);
    using var pe = new PEReader(new MemoryStream(image, writable: false));
    var headers = pe.PEHeaders;
    if (headers.CoffHeader.Machine != Machine.Amd64 || headers.PEHeader is not { } optional)
    {
        throw new BadImageFormatException("Expected an x64 PE executable.");
    }

    var report = patterns.Values.Select(pattern => Scan(pattern, image, headers, optional.SizeOfImage)).ToArray();
    var ok = report.All(result => result.Count == 1 && result.Candidates[0].TargetValid);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        file = Path.GetFileName(file),
        sha256 = Convert.ToHexString(SHA256.HashData(image)),
        machine = headers.CoffHeader.Machine.ToString(),
        imageBase = optional.ImageBase,
        imageSize = optional.SizeOfImage,
        unique = ok,
        patterns = report,
    }, new JsonSerializerOptions { WriteIndented = true }));
    return ok ? 0 : 1;
}
catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or BadImageFormatException or
                           JsonException or ArgumentException or FormatException or OverflowException)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

static SignatureResult Scan(Pattern pattern, byte[] image, PEHeaders headers, int imageSize)
{
    var anchor = Array.IndexOf(pattern.Mask, true);
    if (anchor < 0 || pattern.BytesToSkip < 0 || pattern.BytesToSkip > pattern.Data.Length - sizeof(int))
    {
        throw new BadImageFormatException($"Invalid signature definition: {pattern.Name}");
    }

    // The terrain lookups are constant tables. The remaining references name mutable globals.
    var (targetWidth, needsWrite) = pattern.Name switch
    {
        "Game States" or "File Root" => (sizeof(long), true),
        "AreaChangeCounter" or "GameCullSize" => (sizeof(int), true),
        "Terrain Rotator Helper" or "Terrain Rotation Selector" => (sizeof(byte), false),
        _ => throw new InvalidDataException($"No target requirements for {pattern.Name}."),
    };
    var count = 0;
    var candidates = new List<Match>();
    foreach (var section in headers.SectionHeaders)
    {
        if (!section.SectionCharacteristics.HasFlag(SectionCharacteristics.MemExecute) ||
            section.SizeOfRawData < pattern.Data.Length)
        {
            continue;
        }

        var bytes = image.AsSpan(section.PointerToRawData, section.SizeOfRawData);
        var lastStart = bytes.Length - pattern.Data.Length;
        var cursor = 0;
        while (cursor <= lastStart)
        {
            var next = bytes.Slice(cursor + anchor, lastStart - cursor + 1).IndexOf(pattern.Data[anchor]);
            if (next < 0)
            {
                break;
            }

            var start = cursor + next;
            cursor = start + 1;
            if (!IsMatch(bytes.Slice(start, pattern.Data.Length), pattern))
            {
                continue;
            }

            count++;
            if (candidates.Count < 16)
            {
                var matchRva = section.VirtualAddress + start;
                var disp = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(start + pattern.BytesToSkip, sizeof(int)));
                var displacementRva = matchRva + pattern.BytesToSkip;
                var targetRva = (long)displacementRva + sizeof(int) + disp;
                bool inImage = targetRva >= 0 && targetRva < imageSize;
                var target = inImage ? headers.SectionHeaders.FirstOrDefault(candidate =>
                    targetRva >= candidate.VirtualAddress &&
                    targetRva < (long)candidate.VirtualAddress + candidate.VirtualSize) : default;
                var flags = target.SectionCharacteristics;
                bool valid = inImage && targetRva + targetWidth <= imageSize &&
                    targetRva + targetWidth <= (long)target.VirtualAddress + target.VirtualSize &&
                    flags.HasFlag(SectionCharacteristics.MemRead) &&
                    !flags.HasFlag(SectionCharacteristics.MemExecute) &&
                    (!needsWrite || flags.HasFlag(SectionCharacteristics.MemWrite));
                candidates.Add(new Match(section.Name, matchRva, displacementRva, targetRva, inImage, target.Name, valid));
            }
        }
    }

    return new SignatureResult(pattern.Name, count, candidates);
}

static bool IsMatch(ReadOnlySpan<byte> bytes, Pattern pattern)
{
    for (var i = 0; i < pattern.Data.Length; i++)
    {
        if (pattern.Mask[i] && bytes[i] != pattern.Data[i])
        {
            return false;
        }
    }

    return true;
}

record SignatureResult(string Name, int Count, List<Match> Candidates);
record Match(string Section, int MatchRva, int DisplacementRva, long TargetRva, bool TargetInImage,
    string? TargetSection, bool TargetValid);
record SignatureCandidate(string Name, string Pattern);
