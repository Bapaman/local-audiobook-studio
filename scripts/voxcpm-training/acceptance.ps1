param(
    [Parameter(Mandatory)][string]$BaseUrl,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{32}$')][string]$ProjectId,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{32}$')][string]$CorpusVersionId,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{64}$')][string]$ExpectedManifestSha256,
    [Parameter(Mandatory)][string]$SessionPath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet('Status','Start','Stop','Resume','Exit')][string]$Action = 'Status',
    [ValidatePattern('^[a-f0-9]{32}$')][string]$OperationKey,
    [switch]$ExperimentalConfirmed,
    [switch]$CheckReadiness
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$lasClient = $null

function Read-BoundedJson([string]$Path, [int]$Limit) {
    $file = [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        if ($file.Length -gt $Limit) { throw 'acceptance.file.invalid' }
        $bytes = [byte[]]::new([int]$file.Length); $file.ReadExactly($bytes)
        return [Text.UTF8Encoding]::new($false,$true).GetString($bytes) | ConvertFrom-Json -AsHashtable -Depth 64
    } finally { $file.Dispose() }
}
function Write-NewJson([string]$Path, [object]$Value, [int]$Limit) {
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Value | ConvertTo-Json -Depth 32 -Compress))
    if ($bytes.Length -gt $Limit) { throw 'acceptance.file.invalid' }
    $file = [IO.FileStream]::new($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $file.Write($bytes); $file.Flush($true) } finally { $file.Dispose() }
}
function Request-Api([string]$Method,[string]$Route,[string]$Body='', [string]$Tag='', [string]$Key='', [switch]$HeadersOnly) {
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($Method),$BaseUrl+$Route)
    $response = $null
    try {
        $request.Headers.Add('Cookie',[string]$lasSession.cookieHeader)
        if ($Method -ne 'GET') {
            $request.Headers.Add('Origin',$BaseUrl); $request.Headers.Add('X-CSRF-TOKEN',[string]$lasSession.csrfToken)
            if ($Tag) { $request.Headers.Add('If-Match',$Tag) }
            if ($Key) { $request.Headers.Add('Idempotency-Key',$Key) }
            $request.Content = [Net.Http.StringContent]::new($Body,[Text.Encoding]::UTF8,'application/json')
        }
        $response = $lasClient.SendAsync($request,[Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        $tagValue = if ($null -ne $response.Headers.ETag) { $response.Headers.ETag.ToString() } else { '' }
        if (-not $response.IsSuccessStatusCode) { throw ('acceptance.http.'+[int]$response.StatusCode) }
        if ($HeadersOnly) { return @{etag=$tagValue; data=$null} }
        $budget = [Threading.CancellationTokenSource]::new(60000)
        $stream = $response.Content.ReadAsStream($budget.Token); $buffer=[byte[]]::new(16384); $bodyBytes=[IO.MemoryStream]::new()
        try {
            while (($count=$stream.ReadAsync($buffer,0,$buffer.Length,$budget.Token).GetAwaiter().GetResult()) -gt 0) {
                if ($bodyBytes.Length+$count -gt 8388608) { throw 'acceptance.response.too_large' }
                $bodyBytes.Write($buffer,0,$count)
            }
            $json = [Text.UTF8Encoding]::new($false,$true).GetString($bodyBytes.ToArray())
            $data = if ([string]::IsNullOrWhiteSpace($json)) { $null } else { $json | ConvertFrom-Json -AsHashtable -Depth 64 }
            return @{etag=$tagValue; data=$data}
        } finally { $bodyBytes.Dispose(); $stream.Dispose(); $budget.Dispose() }
    } finally { if ($null -ne $response) { $response.Dispose() }; $request.Dispose() }
}
function Safe-Snapshot($Snapshot) {
    if ($null -eq $Snapshot) { return $null }
    $safe = [ordered]@{}
    foreach ($name in @('id','projectId','revision','state','phase','corpusVersionId','corpusManifestSha256',
        'corpusClipCount','retainedDurationSeconds','globalStep','completedEpochs','nextRetainedOrdinal','nextClipId',
        'targetCompletedEpochs','executionId','canResume','qualityEstablished','diagnosticCode','createdUtc','updatedUtc')) {
        $safe[$name]=$Snapshot[$name]
    }
    $safe.models = @($Snapshot.models | ForEach-Object {
        $model=[ordered]@{}
        foreach ($name in @('id','trainingId','corpusVersionId','manifestSha256','globalStep','completedEpochs','createdUtc')) { $model[$name]=$_[$name] }
        $model
    })
    return $safe
}
try {
    $uri=[Uri]::new($BaseUrl)
    if ($uri.Scheme -cne 'http' -or $uri.Host -cne '127.0.0.1' -or $uri.Port -le 0 -or
        $uri.UserInfo -or $uri.Query -or $uri.Fragment -or $uri.AbsolutePath -ne '/' -or
        $BaseUrl -cne $uri.GetLeftPart([UriPartial]::Authority)) { throw 'acceptance.origin.invalid' }
    $lasSession=Read-BoundedJson $SessionPath 16384
    if ($lasSession.format -ne 1 -or $lasSession.baseUrl -cne $BaseUrl -or
        [string]::IsNullOrWhiteSpace($lasSession.cookieHeader) -or [string]::IsNullOrWhiteSpace($lasSession.csrfToken) -or
        $lasSession.cookieHeader -match '[\r\n]' -or $lasSession.csrfToken -match '[\r\n]') { throw 'acceptance.session.invalid' }
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($OutputDirectory))
    $handler=[Net.Http.HttpClientHandler]::new(); $handler.AllowAutoRedirect=$false; $handler.UseProxy=$false; $handler.UseCookies=$false
    $lasClient=[Net.Http.HttpClient]::new($handler); $lasClient.Timeout=[TimeSpan]::FromSeconds(300)
    $route='/api/projects/'+$ProjectId+'/voxcpm-training'
    $corpus=(Request-Api GET ('/api/projects/'+$ProjectId+'/tts-corpus')).data
    $versions=@($corpus.versions | Where-Object { $_.id -ceq $CorpusVersionId })
    if ($versions.Count -ne 1 -or $versions[0].sha256 -cne $ExpectedManifestSha256) { throw 'acceptance.corpus.mismatch' }
    $current=Request-Api GET $route
    $readiness=$null
    if ($CheckReadiness) { $readiness=(Request-Api GET '/api/voxcpm-training/readiness').data }
    if ($Action -ne 'Status') {
        if ([string]::IsNullOrWhiteSpace($OperationKey)) { throw 'acceptance.operation_key.required' }
        if ($Action -eq 'Start' -and -not $ExperimentalConfirmed) { throw 'acceptance.consent.required' }
        $intentPath=Join-Path $OutputDirectory ('intent-'+$OperationKey+'.json')
        if ([IO.File]::Exists($intentPath)) {
            $intent=Read-BoundedJson $intentPath 16384
            if ($intent.format -ne 1 -or $intent.baseUrl -cne $BaseUrl -or $intent.projectId -cne $ProjectId -or
                $intent.corpusVersionId -cne $CorpusVersionId -or $intent.manifestSha256 -cne $ExpectedManifestSha256 -or
                $intent.action -cne $Action -or $intent.operationKey -cne $OperationKey) { throw 'acceptance.intent.mismatch' }
        } else {
            $commandRoute=$route; $body='{}'; $tag=''
            switch ($Action) {
                Start { $tag=(Request-Api GET ('/api/projects/'+$ProjectId) -HeadersOnly).etag
                    $body=@{corpusVersionId=$CorpusVersionId; experimentalConfirmed=$true} | ConvertTo-Json -Compress }
                {$_ -in 'Stop','Resume'} {
                    if ($null -eq $current.data -or $current.data.corpusVersionId -cne $CorpusVersionId -or
                        $current.data.corpusManifestSha256 -cne $ExpectedManifestSha256) { throw 'acceptance.training.mismatch' }
                    $tag=$current.etag; $commandRoute+='/'+$current.data.id+'/'+$Action.ToLowerInvariant()
                }
                Exit { $commandRoute='/api/app/exit' }
            }
            if ($Action -ne 'Exit' -and $tag -notmatch '^"[0-9]+"$') { throw 'acceptance.etag.invalid' }
            $intent=@{format=1;baseUrl=$BaseUrl;projectId=$ProjectId;corpusVersionId=$CorpusVersionId;manifestSha256=$ExpectedManifestSha256;
                action=$Action;operationKey=$OperationKey;route=$commandRoute;body=$body;etag=$tag}
            Write-NewJson $intentPath $intent 16384
        }
        # A manual repeat reuses the captured request, never a fresh CAS tag or key.
        if ($intent.route -notmatch ('^'+[Regex]::Escape($route)+'(/[a-f0-9]{32}/(stop|resume))?$') -and
            -not ($Action -eq 'Exit' -and $intent.route -ceq '/api/app/exit')) { throw 'acceptance.intent.invalid' }
        $response=Request-Api POST $intent.route $intent.body $intent.etag $OperationKey
        $current=if ($Action -eq 'Exit') { @{data=$null;etag=''} } else { $response }
    }
    $receipt=[ordered]@{format=1;utc=[DateTimeOffset]::UtcNow.ToString('O');action=$Action;projectId=$ProjectId;
        corpusVersionId=$CorpusVersionId;manifestSha256=$ExpectedManifestSha256;readiness=$readiness;training=(Safe-Snapshot $current.data)}
    Write-NewJson (Join-Path $OutputDirectory ('receipt-'+[Guid]::NewGuid().ToString('N')+'.json')) $receipt 1048576
    $receipt | ConvertTo-Json -Depth 32 -Compress
} catch {
    $code=[string]$_.Exception.Message
    if ($code -notmatch '^acceptance\.[a-z0-9_.]+$') { $code='acceptance.failed' }
    [Console]::Error.WriteLine($code)
    exit 1
} finally { if ($null -ne $lasClient) { $lasClient.Dispose() } }
