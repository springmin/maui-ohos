// Slice-scoped global usings.
//
// The platform sources were authored against the standalone compile vehicle, which enabled
// ImplicitUsings. In the real Microsoft.Maui.Core build the OpenHarmony TFM does not enable them,
// so the set the slice relies on is declared here instead. Keeping it in the slice folder means
// every other platform TFM is unaffected (src/MultiTargeting.targets removes the folder).
global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Net.Http;
global using System.Threading;
global using System.Threading.Tasks;
