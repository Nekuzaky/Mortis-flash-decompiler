# Security policy

MortisFlash parses untrusted binary files (SWF files and projector executables). Crashes are expected to be handled
gracefully; anything that could lead to code execution, file access outside the expected folders or a denial of
service through a crafted file is treated as a security issue.

## Supported versions

Security fixes are made in the latest release only.

## Reporting a vulnerability

Please do not open a public issue for a vulnerability. Use GitHub's
[private vulnerability reporting](https://github.com/Nekuzaky/Mortis-flash-decompiler/security/advisories/new) instead, with a
description of the problem and, if possible, a file or steps that reproduce it. You will receive an answer as soon
as possible.

## Embedded player

Games run in Ruffle inside a WebView2 instance that only loads the app's own pages. Navigation away from the player
and links opened by games are blocked, and the game can only read files located in the folder of the opened SWF.
