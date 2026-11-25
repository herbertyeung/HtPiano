# Attribution

The [MIT license](LICENSE) covers the HtPiano client and the public C header `htpiano.h`. The PianoNative DLL has [its own license](sdk/PianoNative/DLL-LICENSE.txt) (also included as `native/DLL-LICENSE.txt` in application builds). Neither license covers the recordings below.

The default piano sound uses stereo Yamaha C5 recordings from **Salamander Grand Piano v3**, by **Alexander Holm** (CC BY 3.0).

- [Pinned SFZ Instruments source and samples](https://github.com/sfzinstruments/SalamanderGrandPiano/tree/3382bf9496bba2486f5ab0de55a264d1dfc38404)
- [Creative Commons Attribution 3.0](https://creativecommons.org/licenses/by/3.0/)
- Bundled in `HtPiano/Assets/SoundBank/instruments.htbank`. Adaptations: selected v9 anchors from C1 to C8; converted to 48 kHz stereo PCM16; capped at 10 seconds and applied a short tail fade. The player shifts between neighboring anchors.
- Full licence and per-sample provenance accompany the bank in `HtPiano/Assets/SoundBank/licenses/YamahaC5/`.

The Harpsichord and Marimba banks use [Versilian Community Sample Library recordings](https://github.com/sgossner/VCSL/tree/c1ea7bcc3c7309650ab0da9d15c9cd1fbc4a4c7e) by Versilian Studios LLC and contributors, CC0 1.0. Per-recording hashes and licence are in `HtPiano/Assets/SoundBank/licenses/Versilian`.
