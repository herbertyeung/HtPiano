# PianoNative SDK · ABI 1

| 文件 / File | 用途 / Purpose | 许可 / License |
| --- | --- | --- |
| [`htpiano.h`](htpiano.h) | C 接口声明 / Public C interface | [MIT](../../LICENSE) |
| [`htpiano.dll`](htpiano.dll) | Windows x64 音源 / Audio engine | [DLL 授权条款 / Binary license](DLL-LICENSE.txt) |

公开的是接口，不是 C++ 音源实现。你可以按 MIT 使用头文件编写自己的调用端。非商业项目可以使用 DLL 并把原版 DLL 随程序一起分发；如果要商用，即使不单独售卖 DLL，也需要另行取得 Herbert Yeung 的授权。

The header is public; the C++ implementation is closed source. You can write your own client using the MIT-licensed header. A non-commercial project may ship the unmodified DLL with its application. Commercial use requires separate authorization from Herbert Yeung, even if the DLL is not sold separately.

构建后，原生库和授权说明位于 `native/htpiano.dll` 和 `native/DLL-LICENSE.txt`。请不要把 DLL 移到 EXE 旁：那里已有同名的 .NET 程序集。

The build copies the native library and its license into the application's `native/` directory. Leave them there: the managed assembly beside the EXE is also named `HtPiano.dll`.
