#Requires -Version 5.1

function ConvertTo-IniTextValue {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [object]$Value
    )

    if ($Value -is [string]) {
        $text = $Value
    } elseif ($Value -is [bool]) {
        $text = $Value.ToString()
    } elseif ($Value -is [System.IFormattable]) {
        $text = $Value.ToString($null, [Globalization.CultureInfo]::InvariantCulture)
    } else {
        throw "Engine profile values must be JSON strings, numbers, or booleans. Unsupported value type: $($Value.GetType().FullName)"
    }
    if ($text -match '[\r\n]') {
        throw 'Engine profile values cannot contain line breaks.'
    }
    return $text
}

function Get-EngineProfileEntries {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProfilePath,

        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$FileMap
    )

    if (-not (Test-Path -LiteralPath $ProfilePath -PathType Leaf)) {
        throw "Engine profile is missing: $ProfilePath"
    }

    try {
        $profile = [IO.File]::ReadAllText($ProfilePath) | ConvertFrom-Json
    }
    catch {
        throw "Engine profile is not valid JSON: $ProfilePath. $($_.Exception.Message)"
    }
    if ($profile -isnot [pscustomobject]) {
        throw 'The engine profile root must be an object grouped by INI filename.'
    }

    $entries = [System.Collections.Generic.List[object]]::new()
    foreach ($fileProperty in $profile.PSObject.Properties) {
        $fileName = [string]$fileProperty.Name
        if ([string]::IsNullOrWhiteSpace($fileName) -or -not $FileMap.Contains($fileName)) {
            $allowed = ($FileMap.Keys -join ', ')
            throw "Unsupported engine profile file '$fileName'. Allowed files: $allowed"
        }
        if ($fileProperty.Value -isnot [pscustomobject]) {
            throw "Engine profile file '$fileName' must contain an object of INI sections."
        }

        foreach ($sectionProperty in $fileProperty.Value.PSObject.Properties) {
            $sectionName = [string]$sectionProperty.Name
            if ([string]::IsNullOrWhiteSpace($sectionName) -or $sectionName -match '[\[\]\r\n]') {
                throw "Engine profile file '$fileName' contains an invalid INI section name: '$sectionName'."
            }
            if ($sectionProperty.Value -isnot [pscustomobject]) {
                throw "Section '$sectionName' in '$fileName' must contain an object of INI keys."
            }

            foreach ($keyProperty in $sectionProperty.Value.PSObject.Properties) {
                $keyName = [string]$keyProperty.Name
                if ([string]::IsNullOrWhiteSpace($keyName) -or $keyName -match '[=\r\n]') {
                    throw "Section '$sectionName' in '$fileName' contains an invalid INI key: '$keyName'."
                }
                if ($null -eq $keyProperty.Value) {
                    throw "Engine profile value '$fileName [$sectionName] $keyName' cannot be null."
                }

                [void]$entries.Add([pscustomobject]@{
                    File = $fileName
                    Path = [string]$FileMap[$fileName]
                    Section = $sectionName
                    Key = $keyName
                    Value = ConvertTo-IniTextValue -Value $keyProperty.Value
                })
            }
        }
    }

    if ($entries.Count -eq 0) {
        throw "Engine profile contains no settings: $ProfilePath"
    }
    return $entries.ToArray()
}
