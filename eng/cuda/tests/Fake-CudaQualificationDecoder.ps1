Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$decoderArguments = @($args)
function Get-ArgumentValue {
    param([Parameter(Mandatory)][string] $Name)

    for ($index = 0; $index -lt $decoderArguments.Count - 1; $index++) {
        if ($decoderArguments[$index] -ieq $Name) {
            return $decoderArguments[$index + 1]
        }
    }
    throw "Missing fake decoder argument '$Name'."
}

if ($decoderArguments.Count -lt 4) {
    throw 'The fake decoder requires backend, input, and output arguments.'
}

$backend = Get-ArgumentValue '--compute-backend'
$payloadMode = Get-ArgumentValue '--payload-mode'
$inputPath = $decoderArguments[-2]
$outputBase = $decoderArguments[-1]
if (-not (Test-Path -LiteralPath $inputPath -PathType Leaf)) {
    throw "Fake decoder input is missing: $inputPath"
}

New-Item -ItemType Directory -Path (Split-Path -Parent $outputBase) -Force | Out-Null
if ($payloadMode -in @('no-output', 'no-files')) {
    # Intentionally emit diagnostics only. The qualification harness must not
    # treat a successful empty inventory as decoder equivalence.
} elseif ($payloadMode -in @('float32-within', 'float32-over')) {
    $samples = [float[]]::new(10000)
    [Array]::Fill($samples, [float] 1.0)
    if ($backend -eq 'cuda') {
        $samples[2468] = if ($payloadMode -eq 'float32-within') {
            [float] (1.0 + 1e-7)
        } else {
            [float] (1.0 + 3e-6)
        }
    }
    $payload = [byte[]]::new($samples.Length * 4)
    [Buffer]::BlockCopy($samples, 0, $payload, 0, $payload.Length)
    [IO.File]::WriteAllBytes("$outputBase.tbc", $payload)
} elseif ($payloadMode -in @(
        'logical-json-same',
        'logical-json-field-difference',
        'logical-json-fileloc-order')) {
    if ($backend -eq 'cpu') {
        $json = '{"videoParameters":{"fieldWidth":910},"fields":[{"fileLoc":100,"syncConf":90},{"fileLoc":200,"syncConf":80}]}'
    } elseif ($payloadMode -eq 'logical-json-field-difference') {
        $json = "{`n  `"fields`": [{`"syncConf`": 91, `"fileLoc`": 100}, {`"syncConf`": 80, `"fileLoc`": 200}],`n  `"videoParameters`": {`"fieldWidth`": 910}`n}"
    } elseif ($payloadMode -eq 'logical-json-fileloc-order') {
        $json = "{`n  `"fields`": [{`"syncConf`": 80, `"fileLoc`": 200}, {`"syncConf`": 90, `"fileLoc`": 100}],`n  `"videoParameters`": {`"fieldWidth`": 910}`n}"
    } else {
        $json = "{`n  `"fields`": [{`"syncConf`": 90.0, `"fileLoc`": 100}, {`"syncConf`": 80, `"fileLoc`": 200}],`n  `"videoParameters`": {`"fieldWidth`": 910}`n}"
    }
    [IO.File]::WriteAllText(
        "$outputBase.tbc.json",
        $json,
        [Text.UTF8Encoding]::new($false))
} elseif ($payloadMode -in @('logical-sqlite-same', 'logical-sqlite-row-difference')) {
    if (-not ('VhsDecode.CudaQualificationFixture.SqliteWriter' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace VhsDecode.CudaQualificationFixture
{
    public static class SqliteWriter
    {
        public static void Write(string path, bool addPhysicalNoise, bool changeRow)
        {
            int status = sqlite3_open(path, out IntPtr database);
            if (status != 0)
            {
                throw new InvalidOperationException("Could not create SQLite fixture.");
            }
            try
            {
                Execute(database, "PRAGMA user_version=3;");
                if (addPhysicalNoise)
                {
                    Execute(database, "CREATE TABLE scratch(value INTEGER); INSERT INTO scratch VALUES(1); DROP TABLE scratch;");
                }
                Execute(database, "CREATE TABLE field_record(file_loc INTEGER PRIMARY KEY, sync_conf INTEGER NOT NULL);");
                Execute(database, "INSERT INTO field_record VALUES(100," + (changeRow ? "91" : "90") + ");");
                Execute(database, "INSERT INTO field_record VALUES(200,80);");
            }
            finally
            {
                sqlite3_close(database);
            }
        }

        private static void Execute(IntPtr database, string sql)
        {
            int status = sqlite3_exec(database, sql, IntPtr.Zero, IntPtr.Zero, out IntPtr error);
            if (status == 0)
            {
                return;
            }
            string message = error == IntPtr.Zero
                ? "unknown SQLite fixture error"
                : Marshal.PtrToStringUTF8(error) ?? "unknown SQLite fixture error";
            if (error != IntPtr.Zero)
            {
                sqlite3_free(error);
            }
            throw new InvalidOperationException(message);
        }

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int sqlite3_open(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string filename,
            out IntPtr database);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_close(IntPtr database);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int sqlite3_exec(
            IntPtr database,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string sql,
            IntPtr callback,
            IntPtr state,
            out IntPtr error);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void sqlite3_free(IntPtr pointer);
    }
}
'@
    }
    [VhsDecode.CudaQualificationFixture.SqliteWriter]::Write(
        "$outputBase.tbc.db",
        $backend -eq 'cuda',
        $backend -eq 'cuda' -and $payloadMode -eq 'logical-sqlite-row-difference')
} elseif ($payloadMode -in @(
        'integer-sparse-lsb',
        'integer-over-rate',
        'integer-two-lsb',
        'integer-unsigned-boundary',
        'integer-signed-ld-rf-tbc')) {
    # 10,000 little-endian 16-bit samples. One changed sample is exactly the
    # allowed 0.01% differing-sample boundary.
    $payload = [byte[]]::new(20000)
    if ($payloadMode -eq 'integer-unsigned-boundary') {
        # CPU 0x7fff and CUDA 0x8000 differ by one only when interpreted as
        # the UInt16 format used by TBC/chroma outputs.
        $boundaryOffset = 1234 * 2
        $payload[$boundaryOffset] = 0xff
        $payload[$boundaryOffset + 1] = 0x7f
        if ($backend -eq 'cuda') {
            $payload[$boundaryOffset] = 0x00
            $payload[$boundaryOffset + 1] = 0x80
        }
    } elseif ($payloadMode -eq 'integer-signed-ld-rf-tbc') {
        # CPU -1 (0xffff) and CUDA 0 differ by 1 in the Int16 format used by
        # LD RF-TBC output, but by 65535 if interpreted as UInt16.
        $boundaryOffset = 4321 * 2
        $payload[$boundaryOffset] = 0xff
        $payload[$boundaryOffset + 1] = 0xff
        if ($backend -eq 'cuda') {
            $payload[$boundaryOffset] = 0x00
            $payload[$boundaryOffset + 1] = 0x00
        }
    } elseif ($backend -eq 'cuda') {
        if ($payloadMode -eq 'integer-sparse-lsb') {
            $payload[2468 * 2] = 1
        } elseif ($payloadMode -eq 'integer-over-rate') {
            $payload[0] = 1
            $payload[9999 * 2] = 1
        } else {
            $payload[0] = 2
        }
    }
    $outputSuffix = if ($payloadMode -eq 'integer-signed-ld-rf-tbc') {
        '.tbc.ldf'
    } else {
        '.tbc'
    }
    [IO.File]::WriteAllBytes("$outputBase$outputSuffix", $payload)
} else {
    $payload = if ($payloadMode -eq 'different' -and $backend -eq 'cuda') {
        [byte[]] @(1, 2, 3, 5)
    } else {
        [byte[]] @(1, 2, 3, 4)
    }
    [IO.File]::WriteAllBytes("$outputBase.bin", $payload)
}

$elapsed = if ($backend -eq 'cuda') { '0.25' } else { '0.50' }
$fps = if ($backend -eq 'cuda') { '8.00' } else { '4.00' }
$backendDetail = if ($payloadMode -eq 'backend-fallback') {
    'cpu (simulated unexpected fallback).'
} elseif ($backend -eq 'cuda') {
    'cuda device 0 (Fake GPU).'
} else {
    'cpu (explicit request).'
}
$timestamp = [DateTimeOffset]::Now.ToString('yyyy-MM-dd HH:mm:ss,fff')
$logLines = @(
    "$timestamp - lddecode - INFO - RF compute backend selected: $backendDetail",
    "$timestamp - lddecode - INFO - Took $elapsed seconds to decode 2 frames ($fps FPS post-setup)"
)
if ($payloadMode -eq 'semantic-duration-difference') {
    $dropoutDuration = if ($backend -eq 'cuda') { '2.0' } else { '1.0' }
    $logLines += "$timestamp - lddecode - WARNING - Dropout duration: $dropoutDuration seconds"
}
if ($payloadMode -ne 'no-files') {
    [IO.File]::WriteAllLines("$outputBase.log", $logLines, [Text.UTF8Encoding]::new($false))
}
if ($payloadMode -eq 'mutate-input' -and $backend -eq 'cpu') {
    $inputBytes = [IO.File]::ReadAllBytes($inputPath)
    if ($inputBytes.Length -gt 0) {
        $inputBytes[0] = $inputBytes[0] -bxor 0xff
        [IO.File]::WriteAllBytes($inputPath, $inputBytes)
    }
}

Write-Output "[$([DateTimeOffset]::Now.ToString('O'))] Completed in $elapsed seconds ($fps FPS)"
if ($payloadMode -ne 'empty-stderr') {
    [Console]::Error.WriteLine("RF compute backend selected: $backendDetail")
    [Console]::Error.WriteLine("Elapsed time: $elapsed seconds")
    if ($payloadMode -eq 'semantic-duration-difference') {
        $dropoutDuration = if ($backend -eq 'cuda') { '2.0' } else { '1.0' }
        [Console]::Error.WriteLine("Dropout duration: $dropoutDuration seconds")
    }
}
