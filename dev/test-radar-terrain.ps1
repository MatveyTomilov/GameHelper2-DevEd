#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $BuildDirectory,
    [Parameter(Mandatory)] [string] $AreaId
)

$ErrorActionPreference = 'Stop'
$runtime = (Resolve-Path -LiteralPath $BuildDirectory).Path
$loader = [Runtime.Loader.AssemblyLoadContext]::Default
[void]$loader.LoadFromAssemblyPath((Join-Path $runtime 'SixLabors.ImageSharp.dll'))
$core = $loader.LoadFromAssemblyPath((Join-Path $runtime 'GameHelper.dll'))
$radar = $loader.LoadFromAssemblyPath((Join-Path $runtime 'Plugins/Radar/Radar.dll'))
$displayName = ''
$catalogued = $core.GetType('GameHelper.Utils.WorldAreaNames')::TryGetDisplayName($AreaId, [ref]$displayName)

# Interior walkable rectangle. The unpacked cells have 2 nibbles per byte, as in the game.
$stride = 10
$width = 2 * $stride
$rows = 12
$walkable = [byte[]]::new($rows * $stride)
$heights = [float[][]]::new($rows)
for ($y = 0; $y -lt $rows; $y++) {
    $heights[$y] = [float[]]::new($width)
    if ($y -lt 3 -or $y -gt 8) { continue }
    for ($column = 3; $column -le 6; $column++) { $walkable[$y * $stride + $column] = 0x33 }
}
$builder = $radar.GetType('Radar.MapEdgeDetector').GetMethod('BuildImage', [Reflection.BindingFlags]'Static, NonPublic')
$image = $builder.Invoke($null, [object[]]@($walkable, $heights, $stride, [float]1, [Numerics.Vector4]::One))
try {
    if (-not $image -or $image.Width -ne $width -or $image.Height -ne $rows) { throw 'Wrong texture dimensions' }
    $pixel = $image.GetType().GetProperty('Item')
    $alpha = [byte[]]::new($rows * $width)
    for ($y = 0; $y -lt $rows; $y++) {
        for ($x = 0; $x -lt $width; $x++) {
            $alpha[$y * $width + $x] = $pixel.GetValue($image, [object[]]@($x, $y)).A
        }
    }
    if ($alpha[2 * $width + 8] -ne 255 -or $alpha[5 * $width + 9] -ne 0 -or $alpha[0] -ne 0) {
        throw 'The terrain rasterizer produced the wrong border or interior'
    }
    [ordered]@{
        areaId = $AreaId; catalogued = $catalogued; displayName = $displayName
        width = $width; height = $rows; opaquePixels = @($alpha | Where-Object { $_ -eq 255 }).Count
        alphaSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($alpha))
        fixture = 'synthetic walkable terrain'
    } | ConvertTo-Json
} finally {
    if ($image) { $image.Dispose() }
}
