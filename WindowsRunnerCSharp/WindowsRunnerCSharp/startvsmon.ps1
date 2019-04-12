Start-Process "rtools_setup_x64.exe" -argumentlist "/install /quiet" -wait
cd "C:\Program Files\Microsoft Visual Studio 15.0\Common7\IDE\Remote Debugger\x64"
.\msvsmon.exe /prepcomputer
.\msvsmon.exe /nostatus /noauth /nosecuritywarn /anyuser /nofirewallwarn /nodiscovery /port 4022
# cd "C:\Assets\"
# .\WindowsRunnerCSharp.exe