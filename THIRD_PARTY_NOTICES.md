# Third-party notices

Developer Control Tower redistributes the following open-source components in
its self-contained Windows packages. Exact dependency graphs are recorded in
the committed `packages*.lock.json` files.

| Component | Version | Licence | Copyright holder(s) |
|---|---:|---|---|
| .NET Runtime and Windows Desktop Runtime | 8.0.30 | MIT and third-party terms | .NET Foundation and Contributors |
| Microsoft.Extensions.DependencyInjection.Abstractions | 8.0.2 | MIT | .NET Foundation and Contributors |
| Microsoft.Extensions.Logging.Abstractions | 8.0.3 | MIT | .NET Foundation and Contributors |
| SSH.NET | 2026.0.0 | MIT | Renci, Oleg Kapeljushnik, Gert Driesen and contributors |
| BouncyCastle.Cryptography | 2.7.0 | MIT | The Legion of the Bouncy Castle Inc. |
| Velopack | 1.2.0 | MIT | Caelan Sayler; Velopack Ltd. |
| YamlDotNet | 16.3.0 | MIT | Antoine Aubry and contributors |

The authoritative .NET Runtime 8.0.30 licence and third-party notices are
redistributed without modification at:

- `licenses/dotnet-runtime-8.0.30/LICENSE.TXT`
- `licenses/dotnet-runtime-8.0.30/THIRD-PARTY-NOTICES.TXT`

They are sourced from the `v8.0.30` tag of
<https://github.com/dotnet/runtime>. SDK 8.0.424 is pinned in `global.json`;
that SDK resolves the self-contained .NET and Windows Desktop runtime packs
to 8.0.30.

Component licence provenance:

- SSH.NET: <https://github.com/sshnet/SSH.NET/blob/2026.0.0/LICENSE>
- BouncyCastle.Cryptography:
  <https://github.com/bcgit/bc-csharp/blob/release-2.7.0/crypto/License.html>
- Velopack: <https://github.com/velopack/velopack/blob/1.2.0/LICENSE>
- YamlDotNet:
  <https://github.com/aaubry/YamlDotNet/blob/v16.3.0/LICENSE.txt>
- Microsoft.Extensions:
  <https://github.com/dotnet/runtime/blob/v8.0.30/LICENSE.TXT>

## MIT licence - SSH.NET

Copyright (c) Renci, Oleg Kapeljushnik, Gert Driesen and contributors

## MIT licence - Velopack

Copyright (c) 2021 Caelan Sayler

Copyright (c) 2024 Velopack Ltd.

## MIT licence - YamlDotNet

Copyright (c) 2008, 2009, 2010, 2011, 2012, 2013, 2014 Antoine Aubry and
contributors

## MIT licence - Microsoft.Extensions

Copyright (c) .NET Foundation and Contributors

All rights reserved.

For each component listed in the four sections immediately above:

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The applicable copyright notice above and this permission notice shall be
included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## MIT licence - BouncyCastle.Cryptography

Copyright (c) 2000-2026 The Legion of the Bouncy Castle Inc.
(https://www.bouncycastle.org).

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sub license, and/or sell
copies of the Software, and to permit persons to whom the Software is furnished
to do so, subject to the following conditions: The above copyright notice and
this permission notice shall be included in all copies or substantial portions
of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

The starter asset library contains only original project content. It does not
bundle vendor icon packs, customer material, presentation templates, or other
third-party assets.
