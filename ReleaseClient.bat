@echo off

set PATH=C:\Program Files\7zip;C:\Program Files\7-Zip\;C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\Tools\;%PATH%

REM Extract binaries if this is a CI build

if exist ffmpeg-master-latest-win64-lgpl-shared.zip (
	echo extracting ffmpeg-master-latest-win64-lgpl-shared.zip
	7z x ffmpeg-master-latest-win64-lgpl-shared.zip -offmpeg || goto error
	copy ffmpeg\ffmpeg-master-latest-win64-lgpl-shared\bin\*.dll Client\FFmpeg\bin\x64\
)

if exist wdi-simple.zip (
	echo extracting wdi-simple.zip
	7z x wdi-simple.zip -owdi-simple
	
	REM turns out we just need the 32bit version
	copy wdi-simple\wdi-simple32.exe Client\runtimes\win\
)

cd Client
dotnet publish -c Release || goto error

call VsDevCmd.bat

cd ..
nuget restore

cd ClientGUI
msbuild ClientGUI.csproj /p:Configuration=Release || goto error

cd ..

move Client\bin\Release\net6.0\SysDVR-ClientGUI.exe Client\bin\Release\net6.0\publish\SysDVR-ClientGUI.exe 
move Client\bin\Release\net6.0\*.dll Client\bin\Release\net6.0\publish\ 

del Client\bin\Release\net6.0\publish\*.pdb

7z a Client.7z .\Client\bin\Release\net6.0\publish\*

:error
exit /B %ERRORLEVEL%
