param(
    [Parameter(Mandatory)][string]$SessionPath,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{32}$')][string]$ProjectId,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{32}$')][string]$ModelId,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{64}$')][string]$ExpectedManifestSha256,
    [Parameter(Mandatory)][ValidateRange(1,512)][long]$ExpectedGlobalStep,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet('Status','Preview','Job','Exit')][string]$Action='Status',
    [ValidatePattern('^[a-f0-9]{32}$')][string]$SegmentId,
    [ValidatePattern('^[a-f0-9]{32}$')][string]$JobId,
    [ValidatePattern('^[a-f0-9]{32}$')][string]$OperationKey,
    [switch]$ExperimentalConfirmed
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$lasClient=$null
function Read-Json([string]$Path,[int]$Limit) {
    $file=[IO.FileStream]::new($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try { if($file.Length -gt $Limit){throw 'acceptance.file.invalid'}
        $bytes=[byte[]]::new([int]$file.Length);$file.ReadExactly($bytes)
        return [Text.UTF8Encoding]::new($false,$true).GetString($bytes)|ConvertFrom-Json -AsHashtable -Depth 32
    } finally {$file.Dispose()}
}
function Save-New([string]$Path,$Value) {
    $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($Value|ConvertTo-Json -Depth 32 -Compress))
    if($bytes.Length -gt 1048576){throw 'acceptance.file.invalid'}
    $file=[IO.FileStream]::new($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try{$file.Write($bytes);$file.Flush($true)}finally{$file.Dispose()}
}
function Api([string]$Method,[string]$Route,[string]$Body='',[string]$Tag='',[string]$Key='',[int]$TimeoutSeconds=300) {
    $request=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($Method),$lasBase+$Route);$response=$null
    $timeout=[Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($TimeoutSeconds))
    try {
        $request.Headers.Add('Cookie',[string]$lasSession.cookieHeader)
        if($Method -ne 'GET') {
            $request.Headers.Add('Origin',$lasBase);$request.Headers.Add('X-CSRF-TOKEN',[string]$lasSession.csrfToken)
            if($Tag){$request.Headers.Add('If-Match',$Tag)};if($Key){$request.Headers.Add('Idempotency-Key',$Key)}
            $request.Content=[Net.Http.StringContent]::new($Body,[Text.Encoding]::UTF8,'application/json')
        }
        $response=$lasClient.SendAsync($request,$timeout.Token).GetAwaiter().GetResult()
        if(-not $response.IsSuccessStatusCode){throw ('acceptance.http.'+[int]$response.StatusCode)}
        $text=$response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        return @{etag=if($null -ne $response.Headers.ETag){$response.Headers.ETag.ToString()}else{''};data=if([string]::IsNullOrWhiteSpace($text)){$null}else{$text|ConvertFrom-Json -AsHashtable -Depth 32}}
    } catch {if($timeout.IsCancellationRequested){throw 'acceptance.http.timeout'};throw
    } finally {if($null -ne $response){$response.Dispose()};$request.Dispose();$timeout.Dispose()}
}
try {
    $lasSession=Read-Json $SessionPath 16384;$lasBase=[string]$lasSession.baseUrl;$uri=[Uri]::new($lasBase)
    if($lasSession.format -ne 1 -or $uri.Scheme -cne 'http' -or $uri.Host -cne '127.0.0.1' -or $uri.Port -le 0 -or $uri.UserInfo -or $uri.Query -or $uri.Fragment -or
        $uri.AbsolutePath -cne '/' -or $lasBase -cne $uri.GetLeftPart([UriPartial]::Authority) -or
        [string]::IsNullOrWhiteSpace($lasSession.cookieHeader) -or [string]::IsNullOrWhiteSpace($lasSession.csrfToken) -or
        $lasSession.cookieHeader -match '[\r\n]' -or $lasSession.csrfToken -match '[\r\n]'){throw 'acceptance.session.invalid'}
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($OutputDirectory))
    $handler=[Net.Http.HttpClientHandler]::new();$handler.AllowAutoRedirect=$false;$handler.UseProxy=$false;$handler.UseCookies=$false
    $lasClient=[Net.Http.HttpClient]::new($handler);$lasClient.Timeout=[Threading.Timeout]::InfiniteTimeSpan;$lasClient.MaxResponseContentBufferSize=8388608
    $training=(Api GET ('/api/projects/'+$ProjectId+'/voxcpm-training')).data
    if($null -eq $training -or $training.projectId -cne $ProjectId){throw 'acceptance.training.mismatch'}
    $matches=@($training.models|Where-Object {$_.id -ceq $ModelId})
    if($matches.Count -ne 1 -or $matches[0].manifestSha256 -cne $ExpectedManifestSha256 -or $matches[0].globalStep -ne $ExpectedGlobalStep){throw 'acceptance.model.mismatch'}
    $job=$null;$jobs=$null
    switch($Action) {
        Status {$jobs=(Api GET ('/api/projects/'+$ProjectId+'/jobs')).data}
        Job {if(-not $JobId){throw 'acceptance.job.required'};$job=(Api GET ('/api/projects/'+$ProjectId+'/jobs/'+$JobId)).data}
        Preview {
            if(-not $ExperimentalConfirmed -or -not $SegmentId -or -not $OperationKey){throw 'acceptance.consent.required'}
            $route='/api/projects/'+$ProjectId+'/jobs';$body=@{kind='preview';segmentIds=@($SegmentId);savedVoxCpmModelId=$ModelId;experimentalConfirmed=$true}|ConvertTo-Json -Compress
            $intentPath=Join-Path $OutputDirectory ('intent-'+$OperationKey+'.json')
            if([IO.File]::Exists($intentPath)) {
                $intent=Read-Json $intentPath 16384
                if($intent.baseUrl -cne $lasBase -or $intent.route -cne $route -or $intent.body -cne $body -or $intent.modelSha256 -cne $ExpectedManifestSha256){throw 'acceptance.intent.mismatch'}
            } else {
                $project=Api GET ('/api/projects/'+$ProjectId)
                if($project.etag -notmatch '^"[0-9]+"$'){throw 'acceptance.etag.invalid'}
                $intent=@{format=1;baseUrl=$lasBase;route=$route;body=$body;etag=$project.etag;modelSha256=$ExpectedManifestSha256}
                Save-New $intentPath $intent
            }
            $job=(Api POST $route $intent.body $intent.etag $OperationKey 900).data
        }
        Exit {[void](Api POST '/api/app/exit' '{}')}
    }
    $receipt=[ordered]@{format=1;utc=[DateTimeOffset]::UtcNow.ToString('O');action=$Action;projectId=$ProjectId;modelId=$ModelId;modelManifestSha256=$ExpectedManifestSha256;
        modelGlobalStep=$ExpectedGlobalStep;trainingState=$training.state;trainingGlobalStep=$training.globalStep;trainingCompletedEpochs=$training.completedEpochs;trainingModelCount=@($training.models).Count;job=$job;jobs=$jobs}
    Save-New (Join-Path $OutputDirectory ('receipt-'+[Guid]::NewGuid().ToString('N')+'.json')) $receipt
    $receipt|ConvertTo-Json -Depth 32 -Compress
} catch {
    $code=[string]$_.Exception.Message;if($code -notmatch '^acceptance\.[a-z0-9_.]+$'){$code='acceptance.failed'}
    [Console]::Error.WriteLine($code);exit 1
} finally {if($null -ne $lasClient){$lasClient.Dispose()}}
