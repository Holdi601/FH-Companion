param(
    [string[]] $ImagePaths = @(),
    [string] $ImageListPath = "",
    [string] $Language = "en"
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

if (-not [string]::IsNullOrWhiteSpace($ImageListPath)) {
    $ImagePaths += Get-Content -LiteralPath $ImageListPath -Encoding UTF8
}
$ImagePaths = @($ImagePaths | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
if ($ImagePaths.Count -eq 0) {
    throw "No image paths were supplied."
}

Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
$null = [Windows.Storage.FileAccessMode, Windows.Storage, ContentType = WindowsRuntime]
$null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics, ContentType = WindowsRuntime]
$null = [Windows.Graphics.Imaging.SoftwareBitmap, Windows.Graphics, ContentType = WindowsRuntime]
$null = [Windows.Graphics.Imaging.BitmapPixelFormat, Windows.Graphics, ContentType = WindowsRuntime]
$null = [Windows.Graphics.Imaging.BitmapAlphaMode, Windows.Graphics, ContentType = WindowsRuntime]
$null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime]
$null = [Windows.Globalization.Language, Windows.Foundation, ContentType = WindowsRuntime]
$null = [Windows.Foundation.IAsyncOperation`1, Windows.Foundation, ContentType = WindowsRuntime]

function Await-WinRt {
    param(
        [Parameter(Mandatory = $true)]
        [object] $AsyncOperation,

        [Parameter(Mandatory = $true)]
        [type] $ResultType
    )

    $asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() |
        Where-Object {
            $_.Name -eq "AsTask" -and
            $_.IsGenericMethodDefinition -and
            $_.GetParameters().Count -eq 1 -and
            $_.GetGenericArguments().Count -eq 1 -and
            $_.ReturnType.Name -eq 'Task`1' -and
            $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
        } |
        Select-Object -First 1

    $task = $asTask.MakeGenericMethod($ResultType).Invoke($null, @($AsyncOperation))
    $task.Wait()
    return $task.Result
}

function ConvertTo-SafeJsonText {
    param([AllowNull()][string] $Text)

    if ($null -eq $Text) {
        return ""
    }

    $safe = [regex]::Replace($Text, "[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]", " ")
    $safe = $safe.Replace('\', '/').Replace([string][char]34, "'")
    return $safe
}

function ConvertTo-JsonStringLiteral {
    param([AllowNull()][string] $Text)

    if ($null -eq $Text) {
        return '""'
    }

    $builder = New-Object System.Text.StringBuilder
    [void]$builder.Append('"')
    foreach ($char in $Text.ToCharArray()) {
        $code = [int][char]$char
        if ([int][char]$char -eq 34) {
            [void]$builder.Append('\"')
            continue
        }
        if ([int][char]$char -eq 92) {
            [void]$builder.Append('\\')
            continue
        }
        if ($char -eq "`b") {
            [void]$builder.Append('\b')
            continue
        }
        if ($char -eq "`f") {
            [void]$builder.Append('\f')
            continue
        }
        if ($char -eq "`n") {
            [void]$builder.Append('\n')
            continue
        }
        if ($char -eq "`r") {
            [void]$builder.Append('\r')
            continue
        }
        if ($char -eq "`t") {
            [void]$builder.Append('\t')
            continue
        }
        if ($code -lt 32 -or $code -eq 127) {
            [void]$builder.Append(('\u{0:x4}' -f $code))
        } else {
            [void]$builder.Append($char)
        }
    }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function ConvertTo-JsonNumberLiteral {
    param([Parameter(Mandatory = $true)] $Value)

    return [System.Convert]::ToString([double]$Value, [System.Globalization.CultureInfo]::InvariantCulture)
}

function ConvertTo-OcrJson {
    param(
        [Parameter(Mandatory = $true)][string] $Image,
        [Parameter(Mandatory = $true)][string] $RecognizerLanguage,
        [Parameter(Mandatory = $true)][int] $Width,
        [Parameter(Mandatory = $true)][int] $Height,
        $TextAngle,
        [Parameter(Mandatory = $true)] $OcrLines
    )

    $builder = New-Object System.Text.StringBuilder
    [void]$builder.Append('{')
    [void]$builder.Append('"image":')
    [void]$builder.Append((ConvertTo-JsonStringLiteral $Image))
    [void]$builder.Append(',"language":')
    [void]$builder.Append((ConvertTo-JsonStringLiteral $RecognizerLanguage))
    [void]$builder.Append(',"width":')
    [void]$builder.Append($Width)
    [void]$builder.Append(',"height":')
    [void]$builder.Append($Height)
    [void]$builder.Append(',"text_angle":')
    if ($null -eq $TextAngle) {
        [void]$builder.Append('null')
    } else {
        [void]$builder.Append((ConvertTo-JsonNumberLiteral $TextAngle))
    }
    [void]$builder.Append(',"lines":[')

    $lineIndex = 0
    foreach ($line in @($OcrLines)) {
        if ($lineIndex -gt 0) {
            [void]$builder.Append(',')
        }
        [void]$builder.Append('{')
        [void]$builder.Append('"text":')
        [void]$builder.Append((ConvertTo-JsonStringLiteral ([string]$line["text"])))
        [void]$builder.Append(',"x":')
        [void]$builder.Append((ConvertTo-JsonNumberLiteral $line["x"]))
        [void]$builder.Append(',"y":')
        [void]$builder.Append((ConvertTo-JsonNumberLiteral $line["y"]))
        [void]$builder.Append(',"width":')
        [void]$builder.Append((ConvertTo-JsonNumberLiteral $line["width"]))
        [void]$builder.Append(',"height":')
        [void]$builder.Append((ConvertTo-JsonNumberLiteral $line["height"]))
        [void]$builder.Append(',"words":[')

        $wordIndex = 0
        foreach ($word in @($line["words"])) {
            if ($wordIndex -gt 0) {
                [void]$builder.Append(',')
            }
            [void]$builder.Append('{')
            [void]$builder.Append('"text":')
            [void]$builder.Append((ConvertTo-JsonStringLiteral ([string]$word.text)))
            [void]$builder.Append(',"x":')
            [void]$builder.Append((ConvertTo-JsonNumberLiteral $word.x))
            [void]$builder.Append(',"y":')
            [void]$builder.Append((ConvertTo-JsonNumberLiteral $word.y))
            [void]$builder.Append(',"width":')
            [void]$builder.Append((ConvertTo-JsonNumberLiteral $word.width))
            [void]$builder.Append(',"height":')
            [void]$builder.Append((ConvertTo-JsonNumberLiteral $word.height))
            [void]$builder.Append('}')
            $wordIndex += 1
        }

        [void]$builder.Append(']}')
        $lineIndex += 1
    }

    [void]$builder.Append(']}')
    return $builder.ToString()
}

function Invoke-OcrImage {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)] $Engine
    )

    $resolvedPath = (Resolve-Path -LiteralPath $Path).Path
    $storageFile = Await-WinRt ([Windows.Storage.StorageFile]::GetFileFromPathAsync($resolvedPath)) ([Windows.Storage.StorageFile])
    $stream = Await-WinRt ($storageFile.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
    try {
        $decoder = Await-WinRt ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
        $bitmap = Await-WinRt ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])

        if ($bitmap.BitmapPixelFormat -ne [Windows.Graphics.Imaging.BitmapPixelFormat]::Bgra8 -or
            $bitmap.BitmapAlphaMode -ne [Windows.Graphics.Imaging.BitmapAlphaMode]::Premultiplied) {
            $bitmap = [Windows.Graphics.Imaging.SoftwareBitmap]::Convert(
                $bitmap,
                [Windows.Graphics.Imaging.BitmapPixelFormat]::Bgra8,
                [Windows.Graphics.Imaging.BitmapAlphaMode]::Premultiplied
            )
        }

        $result = Await-WinRt ($Engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
        $lines = @()
        foreach ($line in $result.Lines) {
            $words = @()
            foreach ($word in $line.Words) {
                $rect = $word.BoundingRect
                $words += [pscustomobject][ordered]@{
                    text = (ConvertTo-SafeJsonText $word.Text)
                    x = [math]::Round($rect.X, 2)
                    y = [math]::Round($rect.Y, 2)
                    width = [math]::Round($rect.Width, 2)
                    height = [math]::Round($rect.Height, 2)
                }
            }

            if ($words.Count -eq 0) {
                continue
            }

            $lineText = ($words | ForEach-Object { $_.text }) -join " "
            $left = ($words | Measure-Object -Property x -Minimum).Minimum
            $top = ($words | Measure-Object -Property y -Minimum).Minimum
            $right = ($words | ForEach-Object { $_.x + $_.width } | Measure-Object -Maximum).Maximum
            $bottom = ($words | ForEach-Object { $_.y + $_.height } | Measure-Object -Maximum).Maximum

            $lines += [ordered]@{
                text = $lineText
                x = [math]::Round($left, 2)
                y = [math]::Round($top, 2)
                width = [math]::Round(($right - $left), 2)
                height = [math]::Round(($bottom - $top), 2)
                words = $words
            }
        }

        return ConvertTo-OcrJson `
            -Image $resolvedPath `
            -RecognizerLanguage $Engine.RecognizerLanguage.LanguageTag `
            -Width $bitmap.PixelWidth `
            -Height $bitmap.PixelHeight `
            -TextAngle $result.TextAngle `
            -OcrLines $lines
    } finally {
        if ($null -ne $stream) {
            $stream.Dispose()
        }
    }
}

$languageObject = [Windows.Globalization.Language]::new($Language)
$engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage($languageObject)
if ($null -eq $engine) {
    $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
}
if ($null -eq $engine) {
    throw "Windows OCR engine is not available for language '$Language'."
}

foreach ($path in $ImagePaths) {
    try {
        [Console]::Out.WriteLine((Invoke-OcrImage -Path $path -Engine $engine))
    } catch {
        $builder = New-Object System.Text.StringBuilder
        [void]$builder.Append('{"image":')
        [void]$builder.Append((ConvertTo-JsonStringLiteral $path))
        [void]$builder.Append(',"error":')
        [void]$builder.Append((ConvertTo-JsonStringLiteral $_.Exception.Message))
        [void]$builder.Append('}')
        [Console]::Out.WriteLine($builder.ToString())
    }
}
