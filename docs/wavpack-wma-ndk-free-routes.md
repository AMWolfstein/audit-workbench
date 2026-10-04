# NDK-free (pure JVM/ART) routes for WavPack and WMA on Android — research notes

Compiled 2026-10-04. Constraint: GPL-3.0 Kotlin player on AndroidX Media3,
minSdk 31, **no NDK, no native code, no FFmpeg**. All routes below run on
JVM/ART-only. Anything not directly verified is flagged **[unverified]**.
Line counts are eyeball estimates where marked *est.*

License shorthand vs GPL-3.0: ✅ compatible / ⚠️ read note / ❌ incompatible.

---

## 0. TL;DR

**WavPack — NDK-free route exists, several of them.**
Best: port the pure-Go **WaxFlow `codec/wavpack`** (MIT) or the pure-Rust
**symphonia-codec-wavpack** (MPL-2.0) to Kotlin — both are 2026-era, modern
coverage, bit-exact-tested. Quick path: Peter McQuillan's 4.40-lineage Java or
C# decoders (BSD) ported/used directly — fine for ordinary 4.x/5.x stereo
files, but 2-channel only, no correction files.

**WMA — an NDK-free route now exists: pure Go.** **WaxFlow** (2026, MIT) has
from-scratch Go decoders for **WMA v1/v2, WMA Pro (5.1/7.1!), WMA Lossless
(partial), and WMA Voice**, plus a Go ASF demuxer — all tested against ffmpeg
**and** Microsoft's own decoders. Port to Kotlin and you're done. No pure
Java/Kotlin/C#/TS/Dart/Swift/Rust WMA decoder exists.

**WASM-on-JVM is real in 2026**: Chicory runs on Android (interpreter works;
compiler has an experimental DexMaker backend; reliable path = build-time
WASM→.class→ dex via AGP → ART JIT). The 70 KB Rockbox-WMA WASM module is a
ready-made payload. NestedVM/Cibyl/asmble are dormant legacy.

**Android platform codecs do not help**: WMA was vendor-only (old Samsungs),
effectively dead since ~Android 11; WavPack never existed in AOSP. Treat both
as absent.

---

## 1. Managed-language decoders

### 1.1 WavPack

#### A. WaxFlow `codec/wavpack` + `container/wv` (Go) ⭐ best modern decoder
- Link: https://github.com/ColeSpringer/WaxFlow · module `github.com/colespringer/waxflow`
  (pkg.go.dev page: https://pkg.go.dev/github.com/colespringer/waxflow/codec/wavpack)
- Files: `codec/wavpack/{decode.go, encode.go, unpack.go, pack.go, words.go,
  checksum.go, wavpack.go}` + tests (`bench_test.go`, `encode_test.go`,
  `encfuzz_test.go`, fuzz corpus); demuxer `container/wv/` (APEv2 tags via
  `container/internal/apev2`, block-index bisection seek, block checksums);
  docs: `THIRD-PARTY-NOTICES.md`, `docs/adr/0001-clean-room-policy.md`.
- Language/size: Go (stdlib only), est. 2–4k LOC for the codec.
- License: **MIT** ✅ (whole repo). Derivation: declared clean-room port of
  libwavpack (BSD-3): entropy coder (`read_words.c`), decorrelation passes /
  weight macros (`unpack.c`, `wavpack_local.h`), metadata sub-block handlers,
  fixed-point exp table; bitreader/demuxer/integration original. Full details
  in THIRD-PARTY-NOTICES.
- Coverage (per commit `fc02070`, 2026-08-19): **lossless v4 streams, 8–32-bit,
  mono/stereo incl. false stereo; bit-exact vs the official WavPack test suite
  (all 48 members) + reference-CLI fixtures at every compression level.**
  **Refuses by name: hybrid, float, DSD, >2 channels.** Also ships an
  *encoder* (4 levels) if you ever want WV writing.
- Seeking/duration: yes — container/wv has block walk + bisection seek
  ("exact seek, fuzz clean"); duration via declared total or length scan.
- Maintenance: **very active**, 124 commits, latest 2026-09-23.
- Perf: "**562× realtime worst case**" in Go (vs a 200× floor). A Kotlin port
  should land in the same order; far beyond need.
- Android effort: translate codec+wv container to Kotlin (est. 1–3 weeks);
  validate with the official 174 MB test suite
  (https://www.wavpack.com/downloads.html → "Decoder test suite") and ffmpeg
  as oracle.

#### B. `symphonia-codec-wavpack` (Rust) — broadest coverage incl. hybrid/float
- Link: https://github.com/pierreaubert/symphonia-add-ons →
  `symphonia-codec-wavpack/` (imported from the author's "SotF" player project).
- Files: `symphonia-codec-wavpack/src/...` (**file names not enumerated** —
  directory opened at root level only) + `README.md`, `CHANGELOG.md`,
  `Cargo.toml`, `LICENSE`.
- Language/size: Rust, fits Symphonia's reader/decoder traits. Size est. 3–6k
  LOC **[unverified]**.
- License: **MPL-2.0** ✅ (GPL-3.0 compat; MPL-2.0's Exhibit-B clause).
- Coverage (README): WavPack blocks/packetization incl. word decoding,
  decorrelation, **joint stereo, false stereo, integer and float samples,
  hybrid lossy streams, embedded correction bitstreams**, trailing metadata,
  **Matroska `A_WAVPACK4`** packets. **DSD: raw mode (mode 0) packed-byte U8
  output only; compressed DSD modes not implemented; external `.wvc`
  correction files not implemented.**
- Seeking/duration: Symphonia model (seek by packet/index) **[details
  unverified]**.
- Maintenance: active — commits through 2026-09-28; CI + changelog added
  2026-09-04. Zero stars; maintained for the author's own player.
- Android effort: similar port effort to A but more arithmetic edge cases
  (float/hybrid). Note Rust int semantics port cleanly to Kotlin.

#### C. Peter McQuillan's official non-C decoders (all BSD, all 4.40-lineage)
All three share the **same limits** (from the C# README): *"will not handle
'correction' files, plays only the first two channels of multi-channel files,
limited in resolution in some large integer or floating point files (but
always provides at least 24 bits), will not accept WavPack files from before
version 4.0."* They decode any ordinary mono/stereo lossless `.wv` (encoder
CLI still writes compatible blocks in v5.x for stereo PCM), ship a WAV-file
demo, and seek via block index. Dormant but stable.
- **Java decoder v1.3** (the one you know): canonical zip
  https://www.wavpack.com/files/JavaWavPackDecoder_v1.3.zip. Package
  structure (from mirrors): `com.wavpack.decoder` —
  `BitsUtils, Bitstream, ChunkHeader, Defines, FloatUtils, MetadataUtils,
  RiffChunkHeader, UnpackUtils, WavPackUtils, WaveHeader, WavpackConfig,
  WavpackContext, WavpackHeader, WavpackMetadata, WavpackStream, WordsUtils,
  decorr_pass, entropy_data, words_data, WvDemo` (+ a Java encoder package).
- **C# decoder** — first-class Kotlin-port source (C# ≈ Java):
  https://github.com/soiaf/C-Sharp-WavPack-Decoder — BSD, © 2010–2016, 2
  commits (Feb 2016 upload), files `Bitstream.cs, BitsUtils.cs, ChunkHeader.cs,
  Defines.cs, ...` (mirrors the Java package; full list not enumerated).
- **haXe decoder v1.4** — the Haxe lead exists after all:
  download zip https://www.wavpack.com/flash/v1.4_haxe_wavpack.zip (BSD);
  **encoder** is GitHub-hosted: https://github.com/soiaf/haXe-WavPack-encoder.
  Haxe syntax port is easy, though C#/Java versions are nearer to Kotlin.
- Links hub: https://www.wavpack.com/downloads.html (official).

#### D. javasound-wavpack (Java, Maven Central)
- Link: https://mvnrepository.com/artifact/com.tianscar.javasound/javasound-wavpack
  · home https://github.com/Tianscar/javasound-wavpack (**404s today ⚠️**).
- "Java implementation of the tiny version of the WavPack 4.40 decoder and
  encoder", BSD-3 ✅, v1.4.2 (2023-05-19), zero recorded dependents. Same
  4.40 limits as C. Usable as a jar (check the sources jar for license file);
  no reason to prefer it over the originals.

#### E. Other languages
- **Dart**: only a `dart:ffi` wrapper of the C tiny decoder
  (https://github.com/JairajJangle/flutter_tiny_wavpack_decoder — WASM on web,
  **CMake/native on Android → violates your constraint**). No pure-Dart
  decoder exists.
- **Swift / Kotlin-native / Python / Haxe-for-WMA**: nothing found (say
  plainly: we found none).
- Python Audio Tools once had WavPack (C-accelerated); dormant repo; not
  pure-Python-hot-path — dead end.

### 1.2 WMA — the only managed-language implementation in existence

#### WaxFlow `codec/{wma,wmapro,wmalossless,wmavoice}` + `container/asf` (Go) ⭐
- Link: https://github.com/ColeSpringer/WaxFlow — dirs:
  - `codec/wma/` — **WMA v1 & v2** (`decode.go`, `bits.go`,
    `tables_coef.go`, `tables_exp.go`, `tables_bands.go`, `tablesgen_test.go`,
    `bench_test.go`, `corpus_test.go`, fuzz seeds — **partial listing**)
  - `codec/wmapro/` — **WMA Pro 0x0162** (`decode.go`, `bands.go`,
    `coefs.go`, `bits.go`, `tables_{bands,scale,coef,decorr}.go`, corpus &
    bench tests — **partial listing**)
  - `codec/wmalossless/` — **WMA Lossless** (`decode.go`, `bits.go`,
    `entropy.go`, corpus/bench tests — **partial listing**)
  - `codec/wmavoice/` — **WMA Voice 0x000A** (CELP-based, 7-cell corpus)
  - `container/asf/` — ASF demuxer (layout/seek/codec wiring; refuses
    Audio-Spread error-correction above span 1)
  - `docs/notes/wma-bitstream.md`, `docs/adr/0001-clean-room-policy.md`,
    `MAINTENANCE.md`, `THIRD-PARTY-NOTICES.md`.
- License: repo **MIT** ✅ — with one nuance: the WMA/Pro/Voice **parameter
  tables** (Huffman books, LSP codebook, band edges, decorrelation matrices)
  are *mechanically extracted* from FFmpeg n9.0 (LGPL-2.1+) `wmadata.h`,
  `wma_freqs.c`, `wmaprodata.h` as "data-only artifacts" under their ADR-0001
  policy (no published WMA spec exists); a `tablesgen_test.go` parses the
  upstream files under a SHA-256 pin so the extraction is auditable. The
  *decoder logic* was written from behavioral notes (`docs/notes/`) without
  opening FFmpeg, and it's validated against **both ffmpeg and Microsoft's own
  Media Foundation decoders** as oracles. Risk framing for you: even under
  the most conservative reading (tables = LGPL-2.1+ material), **LGPL-2.1+ is
  GPL-3.0-compatible** — keep the attribution and you're clean either way.
- Coverage & caveats (from commit messages):
  - wma: WMA 1 & 2 fully; "Decode: WMA 632× → 1226× realtime" after their
    MDCT N/4-point-FFT optimization.
  - wmapro: mono/stereo **and 5.1/7.1** cells; frames spanning packets;
    one frame of lead-in; *"refuses the low-bit-rate tool no decoder outside
    Windows implements"*.
  - wmalossless: **bit-exact vs source PCM in their corpus** — the first
    non-FFmpeg WMA Lossless decoder anywhere; but *"Refuse arithmetic coding,
    LPC, splicing and transmitted CDLMS by name. MCLMS and the cascade order
    ship unverified as named deficits"* → partial coverage; typical
    CD-rip 16/44.1 stereo files are the well-tested center.
  - wmavoice: working CELP decoder with seek run-up support.
- Seeking/duration: yes — ASF seek lands frame-aligned on a decodable object;
  duration from demuxer.
- Maintenance: **active as of last two weeks** (Sep 20–23 2026 commits).
- Android effort: Go→Kotlin port, package by package: v1/v2 core ≈ est. 3–4k
  LOC (2–4 weeks with their corpus + ffmpeg oracle); Pro ≈ similar again;
  Lossless smaller; Voice the most porting surface (float math is fine on
  ART). Table files (`tables_*.go`) port mechanically. **This is your NDK-free
  WMA route.**

### 1.3 Plain statements (searched, not found)
- No pure **Java/Kotlin** WMA decoder exists (decade-long gap; historic
  threads only find the FFmpeg C code).
- No pure **C#** WMA decoder (CSCore's `WMADecoder` wraps Windows Media
  Foundation — OS-native, Windows-only).
- No pure **TypeScript/JavaScript** WMA decoder (only WASM builds of C code,
  which §2 covers, and music-metadata's parser, which you know).
- No pure **Rust** WMA/ASF decoder (Symphonia's codecs table has no WMA/
  WMA Pro/WMA Voice/WMA Lossless rows at all, and no ASF demuxer; no
  community crate found).
- No **Dart/Swift/Haxe/Python** WMA decoder found.

---

## 2. Running the C decoders with no NDK (WASM / bytecode routes)

### 2.1 Chicory — the viable one
- Link: https://github.com/dylibso/chicory (Apache-2.0 ✅; 1.1k★, 1.3k commits,
  latest through May 2026; 1.4.0 made the compiler stable, 1.6.0 added JPMS).
- What it is: zero-dependency **pure-Java WASM runtime** — interpreter +
  compiler (runtime or **build-time** WASM→JVM bytecode).
- Android status:
  - *"Pretty easy to run the Chicory interpreter on Android"* and the full
    WASM spec suite passes on ART — official Dylibso write-up:
    https://blog.evacchi.dev/posts/2025/07/11/wasm-the-hard-way-porting-the-chicory-compiler-to-android/
  - Runtime code-gen on Android needs their **DexMaker backend** (ART can't
    load .class at runtime); it's experimental; they worked through DexMaker
    memory limits but flag large-method fall-back-to-interpreter cases.
  - **Dependable route: build-time AOT.** Compile the .wasm to Java classes
    with Chicory's Maven/Gradle plugin in an app-module; AGP dexes them like
    everything else; ART JITs them at runtime. No dynamic loading anywhere.
  - Third-party integration docs state Chicory is compatible with **Android
    API 28+** (you're minSdk 31 ✓): https://weh.released.at/Integration/Chicory/
  - Project is transitioning to **"Endive"** under the Bytecode Alliance
    (announced ~May/June 2026:
    https://bytecodealliance.org/articles/endive-and-the-next-chapter-of-webassembly-on-the-jvm)
    — track that repo going forward.
- Payloads:
  - WMA: the **70 KB Rockbox libwma WASM** from `@audio/wma-decode`
    (GPL-2.0+ ✅; mod's JS demuxer you'd replace with your own ASF parser, or
    recompile libwma+libasf together to one module).
  - WavPack: no published standalone libwavpack WASM module found — you'd
    build one (clang `-target wasm32`, pure-compute API, feed I/O via
    callbacks; libwavpack is self-contained so this is easy, but it *is* a
    toolchain day).
- Expected performance (your target = stereo 44.1/48 kHz):
  - Build-time-AOT + ART JIT: order-of-magnitude **2–10× native-NDK cost** —
    WMA v2 and WavPack decode have hundreds-of-× headroom (see §4 numbers), so
    comfortably real-time, incl. low-end devices after warmup.
  - Pure interpreter: historically **10–100×** slower than JITted code; likely
    still real-time for WMA v2/WavPack 48k stereo on 2020+ hardware, but tight
    for 24/96, multichannel, or WMA Pro — measure on a weak device. Run the
    decoder on a worker thread with an explicit larger stack (ART interpreter
    frames inflate stack use; default 1 MB thread stacks can overflow —
    called out in the blog).
- Effort: days-to-a-week integration; warm-up and per-callback overhead are
  the gotchas; zero native artifacts. Honest trade: keeps a C lineage in your
  cold path vs. simply §1.2's Go→Kotlin port, which is pure Kotlin forever.

### 2.2 asmble — WASM→JVM bytecode, dormant
- Link: https://github.com/cretz/asmble — builds .class from .wasm (LLVM-
  flavored). Effectively the same idea as Chicory's build-time compiler, but
  archived/unmaintained since ~2020, lagging WASM features.
  License **[unverified — check repo; recall MIT/Apache-2.0-family]**.
  Historical interest; use Chicory instead.

### 2.3 NestedVM / Cibyl / LLJVM — all dead
- NestedVM (http://nestedvm.ibex.org) and Cibyl
  (https://github.com/SimonKagstrom/cibyl): compile C→MIPS→JVM bytecode.
  Both work *technically* on Android (output .class → dex at build time;
  Cibyl was literally built to port C apps to J2ME/Android). Cibyl's author
  notes ~float-poor integer code performs "surprisingly" fine, but 8/16-byte
  memory access is multi-step and slow; overall **≈10–20% of native speed**
  in old write-ups (https://stackoverflow.com/questions/459822). Both dormant
  **15+ years**, toolchains bit-rotted (GCC 3.x-era). LLJVM and the Axiomatic
  commercial compiler likewise dead/bought-and-vanished. Licenses
  **[unverified]**; don't start here in 2026.
- Verdict for this whole bucket: **C→JVM translation is alive only via
  WASM → Chicory.**

---

## 3. Android platform codecs — don't build on them

- **WMA**: not in AOSP (no ASF extractor, no WMA MediaCodec component in
  stock Android). Some OEMs (notably Samsung ~2011–2018) shipped vendor WMA
  decode + ASF extraction, so WMA "worked" on those devices via MediaCodec —
  see https://stackoverflow.com/questions/24818587 and the old demo at
  bigflake/pocketmagic. But Play-Store-era reports show it stopping around
  **Android 11** even on Samsungs
  (https://mediamonkey.com/forum/viewtopic.php?t=98771). No documented mime
  type, no CDD requirement → **unreliable; treat as absent.**
- **WavPack**: has never been in AOSP at any API level **[no evidence found
  of any OEM support either]**. Not in the CDD required list.
- For reference, what Android guarantees (CDD / supported-formats doc):
  PCM/WAVE, FLAC, MP3, AAC (LC/HE), Vorbis, Opus, AMR-NB/WB, MIDI. WavPack
  and WMA are *yours* to provide — which is the whole reason for this
  document.

---

## 4. Performance & effort matrix (stereo 44.1/48 kHz, 16/24-bit, one core)

| Route | Est. effort | Expected real-time headroom* | Notes |
|---|---|---|---|
| Kotlin port of WaxFlow `codec/wavpack` | 1–3 wks | ≥100× (Go bench: 562× worst-case) | modern coverage minus hybrid/DSD/>2ch |
| Kotlin port of `symphonia-codec-wavpack` | 2–4 wks | similar | hybrid/float/correction-in-block covered; no .wvc |
| Use/port McQuillan Java/C# 4.40 decoder | 3–7 days | ≥100× (it ran realtime on 2007 desktops) | stereo-only, no .wvc, ≥24-bit |
| Kotlin port of WaxFlow `codec/wma` | 2–4 wks | est. tens–hundreds of × (Go: 632–1226×) | tables port mechanically; MF+ffmpeg oracles exist |
| Kotlin port of WaxFlow `codec/wmapro` | +2–4 wks | est. tens of × for stereo | multichannel costs ~channels |
| Kotlin port of `codec/wmalossless` | 1–2 wks | high | partial coverage (named deficits) |
| Kotlin port of `codec/wmavoice` | 2–3 wks | fine for voice rates | float CELP; ART handles floats fine |
| Chicory build-time-AOT + Rockbox-WMA WASM | 2–5 days | est. 5–30× after warmup | adds runtime dep; license of wasm = GPL-2.0+ ✅ |
| Chicory interpreter + same WASM | 1–2 days | est. 1–10× — **measure** | startup cost, stack-size, DexMaker caveats |
| NestedVM/Cibyl/asmble | weeks of archaeology | ~10–20% native | **not recommended** |
| Android MediaCodec (WMA) | — | — | not available (see §3) |

*headroom = decode speed ÷ playback speed; est. from referenced benchmarks
and JVM-vs-Go experience — **mark all as estimates, not measurements.**
Kotlin port targets here are minimal: decode 48 kHz stereo using <5% of one
core to leave headroom for Media3/UI.

### Validation assets you get for free
- WaxFlow corpuses (`codec/*/testdata/corpus`, fuzz seeds) + its bench tests.
- Official **WavPack decoder test suite** (174 MB, all 48 members):
  https://www.wavpack.com/downloads.html.
- Oracles: `ffmpeg` CLI (differential), Microsoft's Media Foundation encoder/
  decoder (WaxFlow used both, incl. for things ffmpeg can't decode).

---

## 5. Recommended plan (Media3)

1. **Container layer in Kotlin** (you'll want it regardless):
   - ASF demux from WaxFlow's `container/asf` (or music-metadata/TagLib as
     reference); duration + frame-aligned packet seek.
   - WV block walk from WaxFlow's `container/wv` (checksums, APEv2).
2. **Decoders as Kotlin ports** behind a small `JavCodec`-style API
   (`init(streamInfo)`, `decode(packet)→PCM`, `reset()`):
   - WavPack: WaxFlow `codec/wavpack` first; layer in hybrid/float from
     `symphonia-codec-wavpack` later if needed; .wvc/dsd = out of scope
     initially (nobody offers them managed anyway).
   - WMA v1/v2: WaxFlow `codec/wma`. WMA Pro: `codec/wmapro`. Lossless:
     `codec/wmalossless` (with graceful "unhandled mode" errors mirroring its
     named deficits). Voice: `codec/wmavoice` if you care.
3. **Media3 wiring**: custom `Extractor` per container + `DecoderAudioRenderer`
   around your port (same shape as Media3's FFmpeg/`LibflacAudioRenderer`
   extensions, minus the JNI part — your renderer calls Kotlin directly).
4. **Fallback/bridge option** if ports stall: Chicory build-time-AOT on the
   Rockbox-WMA WASM — keeps you NDK-free at the cost of a runtime dep.
5. Test per §4 assets; run decode on a background thread with an explicit
   stack size.

## 6. Flagged uncertainties (don't read as gospel)
- WaxFlow: single-author, weeks-to-months old, near-zero stars; WMA logic is
  "from notes + tables" clean-room (quality attested by their corpus vs
  Microsoft+ffmpeg oracles, but not by the ecosystem yet). The v1.0 module
  coord on Maven/pkg.go.dev may lag the repo; build from source.
- Symphonia-codec-wavpack `src/` file list, exact block-index/seek API —
  not opened; README lists coverage, not counts.
- Chicory DexMaker compiler backend: described as experimental by its own
  author (July 2025); current chicory version's exact Android module name
  **[unverified]** — confirm in docs before committing.
- Dormant-tool licenses (NestedVM/Cibyl/asmble): not re-checked.
- haXe encoder repo (soiaf) contents/maintenance: not opened.
- javasound-wavpack repo offline; jar provenance must be re-verified before
  shipping.
- All speed numbers are estimates except WaxFlow's own Go benchmarks.
