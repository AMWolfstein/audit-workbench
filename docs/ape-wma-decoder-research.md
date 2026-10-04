# Open-source APE (Monkey's Audio) and WMA/ASF decoders & parsers — research notes

Compiled 2026-10-04 for a GPL-3.0 Kotlin/Android (Media3) player project.
Scope: decode APE and WMA (in ASF) to PCM — without FFmpeg, without the
official Monkey's Audio SDK, JMAC, or anything that depends on them.

License compatibility shorthand vs GPL-3.0:
- ✅ = compatible (MIT, BSD-2/3, Apache-2.0, LGPL-2.0/2.1/3 +, MPL-2.0, GPL-2.0+, GPL-3.0)
- ⚠️ = usable but read the note
- ❌ = not compatible (e.g. GPL-2.0-only, MPL-1.1 alone)

Line counts marked *est.* are eyeball estimates from the file listings, not CLOC output.
Anything I could not verify directly is flagged **[unverified]**.

---

## 1. TL;DR

| Need | Best options today (Oct 2026) | License | Route |
|---|---|---|---|
| APE bitstream → PCM | **golift/ape** (Go, brand-new), **OMBS-IO/ape-decoder** (Rust), **DeaDBeeF ffap** (C), **Rockbox libdemac** (C) | BSD-3 / MIT+Apache / GPL-2.0+ / GPL-2.0+ | JNI the C code now; Kotlin-port the Go or Rust code later |
| ASF container parse | **TagLib** (C++), **VLC libasf** (C), **GStreamer asfdemux** (C), **music-metadata** (TS), **getID3** (PHP), **mutagen** (Py), **Rockbox libasf** (C) | LGPL/MPL / LGPL / LGPL / MIT / pick-a-license / GPL / GPL | Pure-Kotlin port is very doable — do this natively |
| WMA v1/v2 → PCM | **Rockbox libwma**, **DeaDBeeF libwma** (both FFmpeg-derived fixed-point C); **@audio/wma-decode** shows it compiles to 70 KB WASM | LGPL-2.0+ / GPL-2.0+ bundle | JNI now; Kotlin port possible but tedious |
| WMA Pro → PCM | **Rockbox libwmapro** (only option outside FFmpeg) | LGPL-2.1+ | JNI |
| WMA Voice → PCM | **Rockbox libwmavoice** (only option outside FFmpeg) | LGPL-2.1+ | JNI |
| WMA Lossless → PCM | **Nothing exists outside FFmpeg** | — | no non-FFmpeg option |
| Haxe / J2ME leads | **Nothing found** (verified) | — | — |

Recommended architecture: write your own Kotlin ASF/APE container parser
(TagLib/music-metadata as reference — easy), and wrap **libdemac + libwma
(+ libwmapro/libwmavoice)** as a small JNI library fed by your parser, wired
into Media3 the same way Media3's own FFmpeg/FLAC extension modules are built.

---

## 2. APE decoders (bitstream → PCM)

### 2.1 golift/ape — pure Go encoder+decoder ⭐ new
- Link: https://github.com/golift/ape (module `golift.io/ape`)
- Files: `decode.go`, `decode_old.go` (legacy 3.x decoding), `decode_pred.go`,
  `decode_stream.go`, `frame.go`, `format.go`, `predict.go`, `nn.go`, `range.go`,
  `range_table.go`, `float.go`, `prepare.go`, `link.go` (APL link files),
  `tag.go` (APEv2), plus encoder files `encode.go`, `encode_stream.go`.
  Tests included; test strategy: decode compared against reference PCM, encode
  validated by decoding with ffmpeg.
- Language/size: Go, ~20 files, est. 4–6k LOC.
- License: **BSD-3-Clause** ✅ (Go Lift Technologies LLC 2026; the range-model
  tables in `range_table.go` are © Matthew T. Ashland, copied from the
  Monkey's Audio SDK 13.26 — see *legal notes*, §8.2: the SDK has been
  BSD-3-Clause since Aug 2023).
- Coverage: claims **parity with Monkey's Audio 13.26** as of Sep 2026.
  Decode: version 3990 ✓, versions 3.95–3.97 ✓, 3.93/3.94 code path present
  but "needs testing", <3.93 not supported. Encode: all levels
  1000–5000 (fast→insane), 8/16/24/32-bit int + 32-bit float, mono/stereo and
  3–32 channels (i.e. beyond ffmpeg's stereo limit). APL links ✓.
- Seeking/duration: frame-based read + header/seek-table parsing; APL range
  decode. Duration from header ✓.
- Derivation: clean-ish room from the reference SDK (now BSD); only constant
  tables copied (copyright Ashland, BSD).
- Maintenance: **brand new** — 12 commits, first PR merged 2026-09-24, latest
  commit 2026-09-30; by David Newhall (long-time OSS maintainer, but this repo
  has 1 star and no production users yet).
- Port difficulty: **best pure-port candidate if you want zero JNI** — Go has
  almost no platform dependencies, fixed-point/int math maps 1:1 to Kotlin;
  expect a few weeks for a careful port + test vectors. Caveat: young codebase,
  audit against the C implementation before trusting 3.93/3.94.

### 2.2 OMBS-IO ape-decoder — pure Rust decoder ⭐ new
- Link: https://github.com/OMBS-IO/ape-decoder / https://crates.io/crates/ape-decoder
- Files: `src/lib.rs`, `decoder.rs`, `bitreader.rs`, `entropy.rs`,
  `range_coder.rs`, `predictor.rs`, `nn_filter.rs`, `unprepare.rs`, `crc.rs`,
  `format.rs`, `roll_buffer.rs`, `error.rs`, `tag.rs`, `id3v2.rs`
  (+ `examples/`, `fuzz/` with 3 fuzz targets, `tests/`, verification scripts).
- Language/size: Rust, `#![forbid(unsafe_code)]`, 14 src files, est. 4–7k LOC.
- License: **MIT OR Apache-2.0** ✅. "Based on the Monkey's Audio SDK by
  Matthew T. Ashland" (BSD-3 since 2023).
- Coverage: APE **version ≥ 3.95**, all 5 compression levels, 8/16/24/32-bit,
  mono/stereo/**multichannel (up to 8)**, APEv2 + ID3v2 tags, MD5 verify,
  WAV export. Sample-level seeking ✓, range decode ✓, parallel decode ✓.
  127 automated tests; claims byte-identical PCM vs the C++ reference
  (`mac v12.53`) at ~2.6× its speed. Independent validation still thin:
  v0.3.2 (2026-03-18), 30 commits, single author (built for the author's
  "echobox" app); in the Symphonia PR the author acknowledged AI use for
  docs/tests/audit, says core code is his.
- Seeking: yes (frame index + skip), duration ✓.
- Maintenance: last commit 2026-03-18 (quiet for ~7 months, but the author is
  engaged via Symphonia PR #470).
- Port difficulty: Rust struct math is portable; or skip porting and use
  **Mozilla UniFFI** to generate Kotlin bindings for the crate directly —
  realistic alternative to JNI/C. Same "young code" caveat.

### 2.3 DeaDBeeF `ffap` — single-file C decoder (battle-tested)
- Link: https://github.com/DeaDBeeF-Player/deadbeef/tree/master/plugins/ffap
- Files: `ffap.c` (the whole decoder + APE demuxer in one file, est. ~2.8–3k
  lines), `int_neon.S` + `asm.S` (ARM NEON optimizations — already relevant
  for Android), `dsputil_yasm.asm`, `x86inc.asm` (x86 SSE2), `Makefile.am`,
  `COPYING` (GPL-2.0+).
- Language/size: C, fixed-point; designed "no mallocs during decode, fixed
  ringbuffer" — great for an embedded-style JNI lib.
- License: header of `ffap.c`: GPL "version 2 **or any later version**" ✅.
  Lineage (in header): based on FFmpeg `apedec` © 2007 Benjamin Zores, which
  was based on **libdemac by Dave Chapman** (i.e. the direction is
  libdemac → FFmpeg → ffap, not the other way round; the FFmpeg lineage does
  not change the license — it stays GPL-compatible).
- Coverage: `APE_MIN_VERSION 3950`, `APE_MAX_VERSION 3990` (→ v3.95–3.99),
  all 5 compression levels, ≤2 channels, 8/16/24-bit (24-bit merged from
  Rockbox). No 32-bit, no multichannel.
- Seeking/duration: yes (APE seektable), duration from descriptor ✓.
- Maintenance: active — last commit to ffap 2026-03-21; DeaDBeeF 1.10.3
  released 2026-06-10. Used in DeaDBeeF's Android player historically
  (`#ifdef TARGET_ANDROID` shim present in ffap.c; the old Android
  "Free Plugins Pack" shipped the APE decoder + a "heavily modified rockbox
  wma decoder").
- Port difficulty: porting ~3k lines of fixed-point C to Kotlin is doable
  (1–2 weeks + verification) but unattractive vs. the Go/Rust options;
  **JNI is the realistic route** and it's a proven quantity.

### 2.4 Rockbox `libdemac` — the original GPL APE decoder
- Link: https://github.com/Rockbox/rockbox (mirror of git.rockbox.org)
- Files:
  - Library: `lib/rbcodec/codecs/demac/libdemac/` — `demac.h`,
    `decoder.c/.h`, `entropy.c/.h`, `filter.c/.h`, `filter_16_11.c`,
    `filter_32_10.c`, `filter_64_11.c`, `filter_256_13.c`, `filter_1280_15.c`,
    `crc.c`, `parser.c/.h`, `predictor.c/.h`, `rangecoding.h`,
    ARM asm: `predictor-arm.S`, `udiv32_arm.S`, `udiv32_arm-pre.S`,
    `vector_math16_armv5te/_armv6/_armv7.h`, `SOURCES`, `demac_config.h`.
  - Standalone APE→WAV tool + docs: `lib/rbcodec/codecs/demac/{demac.c,wavwrite.c,wavwrite.h,README,COPYING,Makefile}`.
  - Rockbox codec glue (example of feeding the lib with seek support):
    `lib/rbcodec/codecs/ape.c`.
- Language/size: ANSI C, static buffers, est. 5–6k LOC.
- License: **GPL** (README: "libdemac is (C) 2007 Dave Chapman... under the
  GNU GPL", COPYING in that dir is GPLv2; Rockbox files use "version 2 **or
  later**" boilerplate) ✅. Exception noted in README: most of `rangecoding.h`
  is © 1997–2000 Michael Schindler, also GPL. **Not** derived from FFmpeg —
  the causality is reversed (FFmpeg's `apedec` and ffap are derived from
  libdemac).
- Coverage per README (written 2007, conservative): v3.99 all compression
  levels well tested; v3.97 16-bit works, 24-bit "problems in the range
  decoder"; **<3.97 not supported**. Fixes since landed (e.g. the
  mono-silence bug fixed in both Rockbox and FFmpeg in 2017); whether
  3.95/3.96 work today **[unverified]** — treat 3.97+ as the safe floor.
- Seeking/duration: yes via seek table (see `ape.c`), duration ✓.
- Maintenance: Rockbox is maintained (codecs touched 2026; repo active
  Oct 2026), but demac itself is mature/in maintenance mode.
- Port difficulty: modular and clean, but Coldfire/ARM asm and Rockbox
  codeclib dependencies to strip. JNI works (see also: audiojs' 70 KB WASM
  of the WMA sibling proves the codeclib deps are shallow). Pure-Kotlin port
  is feasible but the Go crate is a cleaner source text.

### 2.5 Excluded / not-useful APE sources (for completeness)
- **JMAC** (pure Java, LGPL-2.0) — you already have it; note it uniquely
  covers versions <3.93 that nothing else on this list handles
  (https://sourceforge.net/projects/jmac/).
- **Python Audio Tools** (https://github.com/tuffy/python-audio-tools) —
  *had* Python + C APE decoders; its `TODO` (2015-11) says APE support was
  removed ("I'll re-implement ... Monkey's Audio anyway if I ever get around
  to supporting them (again)"); repo dormant since 2020. Dead end.
- **Kodi ApeCodec, XMMS/GStreamer `mac` plugins, fernandotcl/monkeys-audio** —
  all wrappers around the official SDK you excluded. Note: since
  Monkey's Audio 10.18 (Aug 2023) the SDK itself is **BSD-3-Clause**
  (per the Hydrogenaudio wiki and the official version history) — if you
  ever revisit, it's now license-clean for GPL-3.0, just large.
- **Symphonia** (Rust): upstream Symphonia has **no APE and no ASF/WMA**
  support. PR #470 (symphonia-bundle-ape wrapping the crate in §2.2) is
  **open, not merged** as of Oct 2026; maintainer signaled he'd rather not
  merge external-crate wrappers. Track: https://github.com/pdeljanov/Symphonia/pull/470
- **OxideAV/oxideav-ape**: https://github.com/OxideAV/oxideav-ape — another
  pure-Rust clean-room attempt (2026); per its own README the adaptive
  predictor/delta stage is unfinished; heavily doc/AI-generated. Reference only.
- No pure-JavaScript APE decoder (outside WASM builds of the above) was found.

---

## 3. ASF container parsers (WMA container; metadata/duration/seek layout)

These parse the ASF container your WMA files live in. Writing this layer in
Kotlin yourself is realistic (GUID registry + little-endian object parsing),
and you have *lots* of reference implementations:

### 3.1 TagLib (C++) ⭐ best reference
- Link: https://github.com/taglib/taglib
- Files: `taglib/asf/` — `asffile.cpp/.h`, `asfproperties.cpp/.h`,
  `asftag.cpp/.h`, `asfattribute.cpp/.h`, `asfpicture.cpp/.h`,
  `asfutils.h`; APE headers too: `taglib/ape/` — `apefile.cpp/.h`,
  `apeproperties.cpp/.h`, `apetag.cpp/.h`, `apefooter.cpp/.h`,
  `apeitem.cpp/.h`, `ape-tag-format.txt` (format doc!).
- License: **dual LGPL-2.1 / MPL-1.1** — pick LGPL-2.1 ✅ (MPL-1.1 alone would
  be ❌). ~2–4k LOC for ASF.
- Coverage: ASF/WMA/WMA Pro/WMA Lossless codec identification, bitrate, sample
  rate, channels, **duration**, tags incl. WM/Picture. No PCM (tagger only).
  Doesn't seek (it's a tagger API) but the parsing model is the right one.
- Seeking/duration: duration ✓.
- Maintenance: very active — 2.3.2 released 2026-09-05; ASF got commits
  Sep 2026.

### 3.2 VLC ASF demuxer (C)
- Link: https://github.com/videolan/vlc/tree/master/modules/demux/asf
- Files: `asf.c` (demuxer logic/seeking), `libasf.c/.h` (object parser ~2k
  lines), `asfpacket.c/.h` (packet layer), `libasf_guid.h` (GUID registry).
- License: **LGPL-2.1-or-later** ✅ (verified in `libasf.c` header);
  ~4–5k LOC total.
- Coverage: full ASF incl. packetization, stream selection, seeking via index/
  simple-index objects, preroll; **no codec decoding** (VLC decodes WMA via
  FFmpeg — you only take the demux layer). No DRM.
- Maintenance: very active (commits Sep 2026).
- Note genealogy: VLC asf → Juho Vähä-Herttua's libasf → Rockbox libasf (§3.5).

### 3.3 GStreamer `asfdemux` (C)
- Link: https://github.com/GStreamer/gstreamer/tree/main/subprojects/gst-plugins-ugly/gst/asfdemux
- Files: `gstasfdemux.c/.h` (~4k lines, seeking, VBR, stream setup),
  `asfpacket.c/.h`, `asfheaders.c/.h` (GUID tables), `gstasfelement.c`,
  `gstasf.c`, plus RTP-ASF depayloader files.
- License: **LGPL-2.1-or-later** ✅ (gst-plugins-ugly).
- Maintenance: very active (commits Sep 2026).
- Note: it lives in **gst-plugins-ugly**, the bucket for "good code with
  possible distribution problems (patents)" — relevant to §8: it signals
  historical caution about ASF patents, not a known live claim.

### 3.4 Rockbox `libasf` (C) — minimal packet-layer parser
- Link: https://github.com/Rockbox/rockbox/tree/master/lib/rbcodec/codecs/libasf
- Files: `asf.c`, `asf.h` only (~600–800 lines). Header parsing lives in the
  codec wrapper `lib/rbcodec/codecs/wma.c` (verified: GPL-2.0+, © 2007 Dave
  Chapman) which uses `asf_read_packet()` / `asf_seek()` / `asf_get_timestamp()`.
- License: GPL-2.0-or-later ✅; "based on libasf by Juho Vähä-Herttua, which
  was based on the ASF parser in VLC".
- Coverage: ASF header (in wma.c), data-object packet reading, packet-level
  seeking + timestamps. No DRM.
- Port difficulty: **easiest C code to port to Kotlin** of the C options.

### 3.5 DeaDBeeF WMA plugin glue (C)
- Link: https://github.com/DeaDBeeF-Player/deadbeef/tree/master/plugins/wma
- Files: `asfheader.c` (Rockbox-derived header parser, GPL-2.0+),
  `libasf/` (`asf.c/.h`, packet layer), `wma_plugin.c` (player glue).
- License: GPL-2.0+ ✅. Same VLC→libasf→Rockbox lineage.

### 3.6 music-metadata (TypeScript) ⭐ nicest small reference
- Link: https://github.com/Borewit/music-metadata/tree/master/lib/asf
- Files: `AsfParser.ts`, `AsfObject.ts`, `AsfGuid.ts`, `AsfUtil.ts`,
  `AsfTagMapper.ts`, `AsfLoader.ts` (~1–1.5k lines total).
- License: **MIT** ✅. Coverage: ASF/WMA/WMV tags, codec id, bitrate,
  duration ✓; maps `.wma`/`.wmv`. No decode, no seek index use.
- Maintenance: very active (Sep 2026; v11.15.0).
- TS→Kotlin port is nearly mechanical.

### 3.7 getID3 (PHP)
- Link: https://github.com/JamesHeinrich/getID3
- Files: `getid3/module.audio-video.asf.php` (ASF/WMA/WMV: streams, codec ids,
  duration, tags; ~1.5k lines), `getid3/module.audio.monkey.php` (APE
  properties), plus `module.tag.apetag.php` for APEv2 tags.
- License: **pick one: GPL-3.0 / GPL-2.0 / GPL-1.0 / LGPL-3.0 / MPL-2.0** ✅
  (choose GPL-3.0 or MPL-2.0).
- Maintenance: active (commits Sep 2026).
- PHP is procedural and reads like pseudocode — handy cross-check reference.

### 3.8 mutagen (Python)
- Link: https://github.com/quodlibet/mutagen
- Files: `mutagen/asf/{__init__.py,_objects.py,_attrs.py,_util.py}` (ASF:
  streams, codec, duration, tags); `mutagen/monkeysaudio.py` (verified: 100
  lines, parses APE 3.80–3.99 headers for duration/channels/rate/bits/version
  incl. the old-block-size rules); `mutagen/apev2.py` (APEv2 tags).
- License: **GPL-2.0-or-later** ✅.
- Maintenance: steady (mutagen/asf last touched 2023; project releases
  continue).
- `monkeysaudio.py` is the smallest correct APE-header reference anywhere —
  ideal for your Kotlin duration probe.

### 3.9 Others (secondary; lighter verification)
- **MPlayer** `libmpdemux/demux_asf.c` — GPL-2.0+ ✅; complete ASF demuxer,
  MPlayer dormant-ish (1.5, 2022). Mature but old code.
  **[file exists per MPlayer Makefile; not opened directly]**
- **xine-lib** `src/demuxers/demux_asf.c` — GPL-2.0+; project mostly dormant.
  **[unverified]**
- **libmms** — LGPL; parses ASF headers for MMS streaming + seeking
  (`src/asfheader.c` **[unverified]**); DeaDBeeF's Android pack shipped it for
  MMS. Only relevant if you care about MMS:// radio streams.
- **taglib-sharp / ATL (C#)**: TagLib ports; both cover ASF & APE metadata;
  mono/taglib-sharp (LGPL-2.1+) last commit 2025-05; ATL
  (Zeugma440/atldotnet, MIT) active. C# reads almost like Kotlin — decent
  porting aid. **[exact paths not pinned down for taglib-sharp's current
  `main` layout]**
- **lofty (Rust)**: popular, but note: **lofty parses APE, NOT ASF** (no ASF
  reader — a common wrong assumption; confirmed by downstream projects).
  APE side: tags + properties incl. duration. MIT/Apache-2.0 ✅.
  https://github.com/Serial-ATA/lofty-rs
- **MediaInfo** (BSD-2-Clause) parses ASF & APE deeply — huge C++ codebase;
  fine as *spec* reference, wrong tool to embed. https://github.com/MediaArea/MediaInfoLib **[exact ASF/APE file names unverified]**
- Go: no maintained pure ASF/WMA library found (golift/ape is APE-only).
- Rust: no pure ASF demuxer crate found (Symphonia lacks ASF).

---

## 4. WMA bitstream decoders (ASF packets → PCM)

### 4.1 Rockbox `libwma` (WMA v1/v2) ⭐
- Link: https://github.com/Rockbox/rockbox/tree/master/lib/rbcodec/codecs/libwma
- Files: `wmadeci.c`, `wmadec.h`, `wmadata.h` (VLC/LSP tables, big),
  `wmafixed.c/.h` (fixed-point math incl. FFT/MDCT), `types.h`, `SOURCES`.
  Codec wrapper with seeking: `lib/rbcodec/codecs/wma.c` (verified; drives
  `wma_decode_superframe_*`, uses `asf_seek`).
- License: header carries **"The FFmpeg Project" LGPL, "version 2 or later"**
  (verified in DeaDBeeF's identical copy) ✅. Derived from FFmpeg's
  `wmadec.c` (2002) + saratoga's fixed-point conversion; FFmpeg lineage
  doesn't change terms.
- Coverage: **WMA v1 (0x160) and v2 (0x161)** = WMA 7/8/9 "Standard" — code
  explicitly rejects other codec ids. Fixed-point. Not Pro/Lossless/Voice.
- Seeking/duration: yes (packet seek + timestamps via libasf).
- Maintenance: mature; occasional fixes (last touched 2025–2026 era repo-wide).
- Size/perf: designed for embedded. **Proof of compactness**: see §4.4 (70 KB
  WASM build).
- Port difficulty: ~2.5k lines core + ~2k tables + fixed-point FFT/MDCT.
  Kotlin port is possible (2–4 weeks careful work, with FFmpeg/Rockbox as test
  oracle) — but JNI first is the sane path.

### 4.2 DeaDBeeF `libwma` (same decoder, desktop-flavored copy)
- Link: https://github.com/DeaDBeeF-Player/deadbeef/tree/master/plugins/wma/libwma
- Files: `wmadeci.c`, `wmadec.h`, `wmafixed.c`, `fft-ffmpeg.c`,
  `fft-ffmpeg_arm.h`, `ffmpeg_bitstream.c`, `ffmpeg_get_bits.h`,
  `ffmpeg_intreadwrite.h`, `codeclib_misc.h`; plugin glue `../wma_plugin.c`,
  ASF: `../asfheader.c` + `../libasf/`.
- License: LGPL-2.0-or-later ✅ (plugin bundle GPL-2.0+).
- Coverage: WMA v1/v2 only; detects/flags WMAPro & WMAVoice stream ids but
  can't decode them.
- Seeking: yes, in wma_plugin.c. Maintenance: active (fixes 2026-02).
- Compared with Rockbox: fewer embedded-isms, depends only on libc-ish calls —
  maybe the **easier** of the two libwma trees to lift into a JNI .so.

### 4.3 Rockbox `libwmapro` (WMA Pro) — the only non-FFmpeg WMA Pro decoder
- Link: https://github.com/Rockbox/rockbox/tree/master/lib/rbcodec/codecs/libwmapro
- Files: `wmaprodec.c/.h` (LGPL-2.1+ header, © FFmpeg authors 2007–2009),
  `wma.c/.h`, `wmaprodata.h` (~big tables), `wmapro_math.h`, `quant.h`,
  `mdct_tables.c/.h`, `README.rockbox`.
- License: **LGPL-2.1-or-later** ✅. Per README.rockbox: "files needed from
  ffmpeg's libavcodec/libavutil to build a standalone WMA Professional
  decoder", imported 2010-04-30 (ffmpeg r22886), converted **float→fixed
  point** by Mohamed Tarek.
- Coverage: WMA Pro (codec id 0x162, WMA 9/10 Professional). README says
  **mono/stereo only**; code has `WMAPRO_MAX_CHANNELS 8` behind
  `MEMORYSIZE > 2` (multichannel paths exist but are the less-tested side).
- Seeking/duration: via libasf + wrapper (wrapper file assumed present
  alongside `wma.c` — `wma.c` itself verified; the wmapro/wmavoice wrapper
  entries were not individually opened **[minor]**).
- Maintenance: stable; whitespace cleanups 2026-02.
- Port difficulty: JNI. Considerably bigger fixed-point surface than v1/v2.

### 4.4 Rockbox `libwmavoice` (WMA Voice)
- Link: https://github.com/Rockbox/rockbox/tree/master/lib/rbcodec/codecs/libwmavoice
- Files: `wmavoice.c/.h` **[in SOURCES; main decoder]**, `acelp_filters.c/.h`,
  `acelp_vectors.c/.h`, `avcodec.h`, `libavutil/` shims, `README.rockbox`,
  `Makefile`.
- License: **LGPL** (© 2009 Ronald S. Bultje / FFmpeg) ✅. Per README:
  "files from ffmpeg with minimum modifications to compile standalone",
  imported 2010-08-07 (ffmpeg r24734) — i.e. **floating point** (unusual for
  Rockbox; fine on a modern phone).
- Coverage: WMA Voice (codec id 0x000A) — ACELP-based speech codec.
- Port difficulty: JNI; drags a bit of libavutil; heaviest of the three.

### 4.5 @audio/wma-decode (JavaScript + WASM) — proves the footprint
- Link: https://www.npmjs.com/package/@audio/wma-decode (v1.0.0, 2026-03);
  repo field points to https://github.com/audiojs/wma-decode which **404s as
  of today** ⚠️ (npm package itself is real; repo moved/removed — inspect the
  npm tarball for sources).
- What it is: "ASF demuxer in pure JS, WMA decoding via RockBox fixed-point
  decoder compiled to WASM (**70 KB**)".
- License: **GPL-2.0-or-later** ✅ (per package metadata, inherited from
  Rockbox).
- Coverage: WMA v1/v2 (Rockbox libwma), whole-buffer decode; streaming API
  exists; seek = N/A (whole-file). Useful mainly as evidence: the Rockbox
  decoder with ASF demux compiles to a ~70 KB artifact.

### 4.6 WMA variants with NO non-FFmpeg implementation (be explicit)
- **WMA Lossless (0x163): nothing found.** Only FFmpeg's reverse-engineered
  `wmalossless` decoder exists. If you need it for a Media3 app without
  FFmpeg, your options are: skip the format, or pre-transcode server-side.
- **WMA Pro 5.1+/WMA 10 Pro M1:** only Rockbox's fixed-point port (mono/stereo
  well-tested) — nothing else.
- **WMA Voice:** only FFmpeg + the Rockbox import above.
- **DRM (Protected WMA/PlayReady):** nobody open-source; also separately
  encumbered by anti-circumvention law — treat as out of scope.

---

## 5. Leads from your list — verdicts

| Lead | Verdict |
|---|---|
| Rockbox libdemac (APE) | ✅ real, verified paths; GPL-2.0+; 3.97–3.99 |
| Rockbox libwma (WMA) | ✅ real; LGPL-2.0+; v1/v2; **plus libwmapro & libwmavoice** you may not have known about |
| DeaDBeeF plugins | ✅ real: `plugins/ffap` (APE, GPL-2.0+) and `plugins/wma` (ASF + libwma v1/v2, LGPL); Android precedent |
| Rust crates | ✅ two new APE decoders (§2.1 is Go, §2.2 Rust); lofty=APE-tags only, Symphonia=no APE/ASF (PR #470 open); no Rust ASF/WMA |
| JavaScript/WASM | ✅ `@audio/wma-decode` (Rockbox→WASM, GPL); `music-metadata` TS ASF parser (MIT); no JS APE decoder |
| Go packages | ✅ golift/ape (BSD-3, encode+decode, days old); no Go ASF/WMA |
| J2ME ports | ❌ nothing found (JMAC is J2SE and already known to you) |
| Haxe ports | ❌ nothing found |
| Python Audio Tools | ⚠️ lead dead — APE support removed in 2015, repo dormant |
| FFmpeg / official SDK / JMAC / BASS | excluded as requested (note: SDK turned BSD-3 in Aug 2023) |

---

## 6. Kotlin-port vs JNI — honest assessment

**ASF parsing — write it in Kotlin yourself.** It's GUID-keyed little-endian
object parsing; music-metadata (MIT, ~1.2k LOC) and TagLib/asf (~3k LOC) are
clean references; DeaDBeeF's `asfheader.c` + Rockbox `libasf/asf.c` show the
packet layer incl. seek math (~1k LOC). Expect ~1–2k LOC Kotlin, fully
testable with real files. You're building an Extractor anyway.

**APE decode — JNI first, port later if you want.**
- JNI plastic: Rockbox `libdemac` or DeaDBeeF `ffap.c` (both ARM-optimized,
  fixed-point, small; build with the NDK for arm64-v8a only — minSdk 31).
- Pure-Kotlin path: port **golift/ape** (BSD-3, pure function-shaped codec,
  widest coverage incl. 32-bit & multichannel) — plan for 2–4 weeks of careful
  int-arithmetic work, verifying PCM hashes against the reference outputs.
- Middle path: UniFFI-bind the Rust crate.

**WMA v1/v2 decode — JNI, realistically.** The decoder core is ~2.5k LOC +
fixed-point FFT/MDCT + large VLC/LSP tables; a Kotlin port is doable but the
least rewarding code to hand-port (bit-exact validation needed). Wrap
Rockbox/DeaDBeeF `libwma` as `libwmadec.so` with a tiny API
(init(waveformatex), decode(packet)→PCM), feed packets from your Kotlin ASF
parser — exactly the Rockbox `wma.c` design (~200 lines of glue logic to
mirror). Add `libwmapro`, `libwmavoice` .sos separately if you want them.

**Media3 wiring.** Mirror Media3's own native-extension architecture:
a custom `Extractor` (ASF/APE) emitting the compressed packets + a
`DecoderAudioRenderer` around your JNI decoder (pattern: Media3's
`LibflacAudioRenderer` / `FfmpegAudioRenderer` extension modules), or decode
off-band through the Transformer pipeline. Both decoders here expose
frame/packet-based APIs that fit that shape.

---

## 7. License compatibility matrix (GPL-3.0 app)

| Project | License | GPL-3.0 ⊕? | Notes |
|---|---|---|---|
| golift/ape | BSD-3 | ✅ | tables © Ashland (BSD SDK) |
| ape-decoder (Rust) | MIT OR Apache-2.0 | ✅ | Apache-2.0 ⇄ GPL-3.0 ok (not GPL-2) |
| ffap (DeaDBeeF) | **GPL-2.0-or-later** | ✅ | "or later" is the key word — upgrade to v3 on combination |
| Rockbox demac/libasf/wma.c | **GPL-2.0-or-later** | ✅ | same |
| Rockbox/DeaDBeeF libwma | LGPL-2.0-or-later | ✅ | |
| Rockbox libwmapro, libwmavoice | LGPL-2.1-or-later | ✅ | |
| VLC libasf, GStreamer asfdemux | LGPL-2.1-or-later | ✅ | |
| TagLib | LGPL-2.1 **or** MPL-1.1 | ✅ via LGPL | MPL-1.1 side alone would be ❌ |
| music-metadata | MIT | ✅ | |
| getID3 | GPLv1/2/3, LGPLv3, MPL-2.0 (pick) | ✅ | |
| mutagen | GPL-2.0-or-later | ✅ | |
| MPlayer demux_asf | GPL-2.0-or-later | ✅ | |
| Symphonia (if PR merges) | MPL-2.0 | ✅ | MPL-2.0 ⇄ GPL-3.0 ok |
| Monkey's Audio SDK (excluded) | was proprietary → **BSD-3 since 10.18 (2023-08-10)** | ✅ now | historically the blocker; no longer is |

For a single-process Android app, LGPL pieces must remain replaceable —
standard practice: keep each LGPL lib as its own `.so` built from unmodified
(or published) sources and document it in About/Open-source-licenses.

---

## 8. Patents & legal: shipping a WMA decoder in 2026

**Not legal advice. The practical picture:**

1. **Gold standard of caution**: patents on WMA v1/v2 (1999–2003),
   WMA Pro (2003), WMA Lossless (2003) and WMA Voice (2004) would have been
   filed ~1997–2006. US utility patents last 20 years from filing →
   the core WMA patent estate is **expired by ~2023–2026**. Same story as
   MP3 (Fraunhofer terminated its MP3 program in 2017 once patents lapsed).
2. **Decades of unpunished open distribution**: FFmpeg's reverse-engineered
   WMA decoders (2002+) ship in Debian main, Fedora (via free builds), VLC,
   DeaDBeeF, Rockbox, browsers-based players etc., with no known Microsoft
   enforcement against decoders. WMA Lossless/Pro/Voice were specifically
   reverse-engineered by FFmpeg (per Wikipedia) and also shipped for years.
3. **Historical counter-signal**: Microsoft did once pressure an open-source
   project over ASF — VirtualDub dropped ASF support after a Microsoft legal
   complaint circa 2000 (contemporaneous discussion:
   https://linux.slashdot.org/story/03/01/29/1934211-mplayer-licence-trouble-with-a-twist).
   And GStreamer still keeps `asfdemux` in the **-ugly** repo (the "possible
   distribution problems" bucket) out of jurisdiction caution. Microsoft also
   used to run a "Windows Media Components" licensing program
   (referenced e.g. by the US Library of Congress format description,
   https://www.loc.gov/preservation/digital/formats/fdd/fdd000093.shtml).
   I found **no evidence that program is live for WMA decoders in 2026**
   — but treat that as "couldn't verify either way", not as a legal opinion.
4. **Distinctions that matter**: (a) decoders vs encoders — encoder-side
   licensing terms were always stricter and expire later; you only decode;
   (b) the *format spec* itself is propri‌etary — all open implementations
   here are reverse-engineered, which is what the VirtualDub case was about;
   (c) **DRM**: "protected" WMA (PlayReady/WM-DRM) is out of scope entirely —
   no open implementation exists and circumvention law (DMCA §1201) is a
   separate, live issue regardless of patents; (d) WMA Voice builds on
   ACELP (speech-codec patents, also ~2004-era, likewise lapsed/expiring);
   (e) you've said GPL-3.0: GPLv3's patent clauses bind *contributors to your
   project*, they don't shield you from third-party patents — neutral here.
5. **Bottom line for Oct 2026**: for v1/v2 (and Pro/Voice/Lossless) decoding
   in an open-source app, all core patents have expired by now or are at the
   very end of their terms, and two decades of unchallenged open distribution
   makes enforcement essentially unthinkable for a decoder. The residual
   risks are (i) unknown submarine patents (unquantifiable, same as any
   codec), and (ii) DRM'd files — exclude those. If you ever sell the app or
   ship encoder features, get real advice.
   (Useful background search trail: AAC/H.264 "baseline patent" reasoning in
   https://law.stackexchange.com/questions/96133.)

APE legal side is simpler: the format was never patent-asserted; the old
blocker was the SDK's proprietary license, removed Aug 2023 (BSD-3).
All the independent implementations above are separately copyright-clean
under their stated licenses.

---

## 9. Maintenance status snapshot (checked 2026-10-04)

| Project | Last activity seen |
|---|---|
| Rockbox | commits 2026-10-03 (demac/libwma stable years) |
| DeaDBeeF ffap / wma | 2026-03 / 2026-02; release 1.10.3 (2026-06) |
| golift/ape | 2026-09-30 (days old!) |
| ape-decoder (Rust) | v0.3.2, 2026-03-18 |
| Symphonia PR #470 | open since 2026-03-18, not merged |
| TagLib | 2.3.2, 2026-09-05 |
| VLC asf module | commits 2026-09 |
| GStreamer asfdemux | commits 2026-09 |
| music-metadata | 2026-09-22 |
| getID3 | 2026-09-27 |
| mutagen | steady (asf module 2023, project alive) |
| MPlayer | 1.5 (2022); minimal |
| Python Audio Tools | 2020 (dormant) |
| libmms/xine | dormant (2014–2022) |

## 10. Flagged uncertainties (recap, so nothing reads as gospel)
- All LOC numbers are estimates.
- golift/ape and ape-decoder: verified to exist and build/tests claimed by
  authors; **no independent third-party validation yet** (both are weeks/
  months old).
- @audio/wma-decode: npm package verified; its GitHub repo URL 404s today.
- libmms/xine file paths not opened; MPlayer's demux_asf.c confirmed only via
  its Makefile listing.
- Rockbox libdemac behavior on 3.95/3.96 files; wmapro/wmavoice wrapper file
  names; taglib-sharp current directory layout — not individually verified.
- Whether any Microsoft WMA codec patent program technically still exists —
  no live page found; no termination notice found either.
