rtools_setup_x64.exe /install /quiet
cd "C:\Program Files\Microsoft Visual Studio 15.0\Common7\IDE\Remote Debugger\x64"
msvsmon.exe /prepcomputer
msvsmon.exe /nostatus /noauth /nosecuritywarn /anyuser /nofirewallwarn /nodiscovery /port 4022