# Third-Party Notices and Licenses

## Dedication

First and foremost, all my apps, vibecoded assisted and otherwise, are dedicated to Jenni and Diana, until my end of life, Someone takes over one of my apps or projects, or either of them are no longer with me or become unfaithful.

---

## TimePlayer's own license

TimePlayer's own code (the application, its NVDA add-on, the build scripts and the tests) is released under the
**BSD Zero Clause License (0BSD)**; see [LICENSE](../LICENSE). Anyone may use, copy, change and share it, for any
purpose, without having to keep a notice.

That license covers only TimePlayer's own code. The components below keep their own licenses, which still apply to
them when they are shipped with TimePlayer:

- mpv and yt-dlp are separate programs. TimePlayer starts them as their own processes and talks to them over
  pipes and command lines; it does not link them into its code. Shipping them next to TimePlayer is the "mere
  aggregation" the GNU GPL allows, so the GPL applies to mpv but does not extend to TimePlayer.
- The NVDA Controller Client is an LGPL library that TimePlayer loads at run time. The LGPL permits this from
  software under any license, as long as the library itself stays under the LGPL and can be replaced.
- The NVDA add-on runs inside NVDA, which is GPL-2.0 licensed. 0BSD is compatible with the GPL, as NV Access asks of add-ons.
- The remaining libraries are under the MIT license, which only asks that their notices be kept, as they are here.

---

## 1. yt-dlp

* **Project**: yt-dlp
* **URL**: https://github.com/yt-dlp/yt-dlp
* **License**: The Unlicense (Public Domain Dedication)
* **Shipped as**: `bin\yt-dlp.exe`, run as a separate program.

The Windows build of yt-dlp contains Python and other libraries under their own licenses; they are listed in
yt-dlp's [THIRD_PARTY_LICENSES.txt](https://github.com/yt-dlp/yt-dlp/blob/master/THIRD_PARTY_LICENSES.txt).

```
This is free and unencumbered software released into the public domain.

Anyone is free to copy, modify, publish, use, compile, sell, or
distribute this software, either in source code form or as a compiled
binary, for any purpose, commercial or non-commercial, and by any
means.

In jurisdictions that recognize copyright laws, the author or authors
of this software dedicate any and all copyright interest in the
software to the public domain. We make this dedication for the benefit
of the public at large and to the detriment of our heirs and
successors. We intend this dedication to be an overt act of
relinquishment in perpetuity of all present and future rights to this
software under copyright law.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
IN NO EVENT SHALL THE AUTHORS BE LIABLE FOR ANY CLAIM, DAMAGES OR
OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE,
ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
OTHER DEALINGS IN THE SOFTWARE.

For more information, please refer to <http://unlicense.org/>
```

---

## 2. mpv media player

* **Project**: mpv
* **URL**: https://mpv.io
* **License**: GNU General Public License version 2 or later (GPL-2.0-or-later); parts under LGPL-2.1-or-later
* **Shipped as**: `bin\mpv\mpv.exe` and `bin\mpv\d3dcompiler_43.dll`, run as a separate program.

TimePlayer controls mpv as an independent process through its documented JSON IPC interface
(`--input-ipc-server`) and shows mpv's video in its window through a Win32 window handle (`--wid`). No mpv code
is linked into TimePlayer.

The mpv build shipped with TimePlayer is shinchiro's Windows build of 17 October 2026, mpv commit `dde0671072`.
It includes FFmpeg and other libraries under the GPL and LGPL.

* Build used: https://github.com/shinchiro/mpv-winbuild-cmake/releases/tag/20261017
* Build scripts and the list of included libraries: https://github.com/shinchiro/mpv-winbuild-cmake
* mpv source: https://github.com/mpv-player/mpv (commit `dde0671072`)
* FFmpeg source: https://ffmpeg.org/download.html

`d3dcompiler_43.dll` is Microsoft's redistributable Direct3D shader compiler, included in the mpv build.

```
This program is free software; you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation; either version 2 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
GNU General Public License for more details.
```

The full GPL-2.0 text: https://www.gnu.org/licenses/old-licenses/gpl-2.0.html

---

## 3. NVDA Controller Client

* **Project**: NonVisual Desktop Access (NVDA) Controller Client
* **URL**: https://github.com/nvaccess/nvda (source in `extras/controllerClient`)
* **Author**: NV Access Limited and contributors
* **License**: GNU Lesser General Public License version 2.1 (LGPL-2.1)
* **Shipped as**: `nvdaControllerClient64.dll`, unmodified.

TimePlayer loads the library at run time through P/Invoke (`nvdaController_testIfRunning`,
`nvdaController_speakText` and `nvdaController_cancelSpeech`) to speak captions through NVDA. It does not change
the library, and it keeps working, without NVDA speech, if the library is replaced or removed.

The full LGPL-2.1 text: https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html

---

## 4. NVDA

* **Project**: NonVisual Desktop Access
* **URL**: https://github.com/nvaccess/nvda
* **License**: GNU General Public License version 2 (GPL-2.0)

TimePlayer's braille add-on (`TimePlayerBraille.nvda-addon`) runs inside NVDA and uses NVDA's add-on interfaces.
NVDA itself is not shipped with TimePlayer. The add-on's own code is under 0BSD, which is compatible with the GPL.

---

## 5. CommunityToolkit.Mvvm

* **Project**: .NET Community Toolkit
* **URL**: https://github.com/CommunityToolkit/dotnet
* **License**: MIT License

```
Copyright (c) .NET Foundation and Contributors.
All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

---

## 6. Microsoft .NET runtime and Microsoft.Extensions libraries

* **Project**: .NET
* **URL**: https://github.com/dotnet
* **License**: MIT License

```
The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

---

## 7. Bouncy Castle Cryptography (BouncyCastle.Cryptography)

* **Project**: The Legion of the Bouncy Castle C# library
* **URL**: https://www.bouncycastle.org/csharp/
* **License**: MIT License
* **Used for**: checking the Ed25519 signatures on updates.

```
Copyright (c) 2000-2024 The Legion of the Bouncy Castle Inc. (https://www.bouncycastle.org)

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute,
sub license, and/or sell copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial
portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT
LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO
EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR
THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

---

## 8. SponsorBlock

* **Project**: SponsorBlock, by Ajay Ramachandran and contributors
* **URL**: https://sponsor.ajay.app (source: https://github.com/ajayyy/SponsorBlock)
* **Used for**: skip segments, read from its public API. No SponsorBlock code is included.
* **Data license**: the SponsorBlock database is available under Creative Commons Attribution-NonCommercial-ShareAlike 4.0
  (https://creativecommons.org/licenses/by-nc-sa/4.0/). TimePlayer is free and non-commercial, and credits SponsorBlock
  wherever its segments are used.

---

## 9. Return YouTube Dislike

* **Project**: Return YouTube Dislike
* **URL**: https://returnyoutubedislike.com (API: https://returnyoutubedislikeapi.com)
* **Used for**: estimated dislike counts, read from its public API with attribution, as its API documentation asks.
  No Return YouTube Dislike code is included.

---

## 10. Inno Setup

* **Project**: Inno Setup, by Jordan Russell and Martijn Laan
* **URL**: https://jrsoftware.org/isinfo.php
* **License**: Inno Setup License (free for any use, including commercial)

The installer, `TimePlayer_setup.exe`, is built with Inno Setup.
Its Romanian translation (`build/Languages/Romanian.isl`, by Alexandru Bogdan Munteanu) comes from the Inno Setup
repository's unofficial translations, under the same license.

---

## 11. LRCLIB

* **Project**: LRCLIB, a free and open lyrics database
* **URL**: https://lrclib.net
* **Used for**: song lyrics, with timing, read from its public API. Only a song's artist, title and length are sent.
  No LRCLIB code is included.

---

## 12. System.Security.Cryptography.ProtectedData

* **Project**: part of .NET, by Microsoft
* **URL**: https://github.com/dotnet/runtime
* **License**: MIT
* **Used for**: keeping the optional YouTube sign-in encrypted for the Windows user (Windows DPAPI).
