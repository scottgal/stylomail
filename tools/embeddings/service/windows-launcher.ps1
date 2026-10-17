# Start the embedding service from Windows so it outlives the ssh channel that asks for it.
#
# Why this exists: the service runs in WSL, and a process started inside a WSL session is torn down
# shortly after the last wsl.exe client disconnects. Measured on this host: nohup, a systemd --user
# unit with Linger=yes, and a detached tmux server each died within about a minute. A wsl.exe owned
# by a Windows process survives, and this script creates one.
#
# Every ArgumentList element below is a path or a flag with no spaces. Do not add a shell command
# with spaces here: Start-Process joins the array with spaces and does not quote, so bash would
# receive only the first word. The redirection lives in background.sh instead.
$ErrorActionPreference = 'Stop'

$exe = Join-Path $env:SystemRoot 'System32\wsl.exe'
$arguments = @(
    '-d', 'Ubuntu',
    '--',
    '/bin/bash', '/home/scott/stylomail-embed/background.sh'
)

$process = Start-Process -PassThru -WindowStyle Hidden -FilePath $exe -ArgumentList $arguments
"started wsl.exe pid $($process.Id) at $(Get-Date -Format o)" |
    Out-File -Encoding ascii 'C:\Users\scott\emb2-launch.log'
