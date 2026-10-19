# Peel: paid features we can build free

Research date: 2026-10-19. Scope: features that comparable tools sell behind a
paid tier, subscription, license or per-use credits, and that Peel could run
for free, offline, on George (Windows 11, Ryzen 7 9800X3D, RTX 4060 8 GB, about
32 GB RAM). Everything below is already-planned-aware: SPEC.md covers plain
format conversion, compress, crop, trim, merge, split, redact (images and
video), metadata, float and copy path, so those are not repeated here. Cloud
API features are excluded.

Pricing notes: most vendors hide prices behind dynamic pages, so several
figures come from third-party aggregators and are marked as such. Treat them as
order-of-magnitude, not quotes. Tangerine itself states that "file actions do
not use your conversion allowance", which confirms a metered conversion
allowance exists, but its site lists no price or allowance size
([tangerineformac.com](https://tangerineformac.com/)). Peel already removes
that meter (SPEC: "No conversion allowance, no paywall").

License flags used below: **NC** = non-commercial weights or code (fine for
Daniel's personal use, blocks any future redistribution or sale). **AGPL** =
network copyleft (fine for a local personal app; matters only if Peel is ever
distributed with it linked in). Note that SPEC.md already depends on PyMuPDF,
which is AGPL-3.0 (or a paid Artifex license), and ocrmypdf pulls in
Ghostscript, also AGPL.

## Candidate features

Sorted by value (high, med, low), then effort (S, M, L). Wheel names follow
SPEC.md: Shift = conversions, Ctrl = file tools, Alt = file actions.

| # | Feature | Who charges for it, roughly how | Free local building block (license) | Feasibility on George | Wheel UX | Value | Effort |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | Remove image background | remove.bg: about $9 for 40 credits/month, $39 for 200 (aggregators) [1]; Canva background remover is Pro-only [2]; Adobe Express refine brush is Premium [3] | rembg (MIT) running BiRefNet or BiRefNet-lite ONNX (MIT) [4] on onnxruntime-gpu | BiRefNet 1024px about 0.3 to 1 s/image on the 4060 with CUDA, 3 to 8 s on CPU; under 2 GB VRAM | Ctrl wheel, Image: "Remove background", single drop, writes `<name> (no bg).png`. Chains with the planned "Add background" | high | S |
| 2 | OCR: searchable PDF, image to text | Acrobat Pro $19.99/mo; Standard lacks OCR and redaction [5]; iLovePDF Premium for OCR to Word/Excel/PPT [6]; ABBYY FineReader Standard about $99/yr [7]; CleanShot X and Shottr ship OCR in paid licenses [8] | ocrmypdf (MPL-2.0) + Tesseract (Apache-2.0) for PDFs; PaddleOCR (Apache-2.0) or RapidOCR for screenshots and photos | CPU bound; 8 cores do about 1 to 3 s/page with ocrmypdf `--jobs 8`; PaddleOCR on GPU is near instant per image | Ctrl wheel, PDF: "Make searchable (OCR)" writes `<name> (OCR).pdf`. Ctrl wheel, Image: "Copy text" puts text on clipboard and toasts it. Also feed OCR into the planned PDF to DOCX path for scanned input | high | S |
| 3 | Transcribe audio/video to SRT, VTT, TXT | Otter Pro $16.99/mo for 1,200 min (aggregator) [9]; Descript Hobbyist $16/user/mo annual, Creator $24 with 10 h transcription [10]; CloudConvert meters conversion minutes [11] | faster-whisper (MIT, CTranslate2) with Whisper large-v3-turbo weights (MIT); whisper.cpp (MIT) as fallback | large-v3-turbo float16 fits in about 3 to 4 GB VRAM and runs far faster than real time on a 4060; CPU int8 still usable for short clips | Shift wheel, Audio and Video: new targets SRT, VTT, TXT ("Transcribe"). Language auto-detect, Spanish and English both fine. Ctrl wheel, Video: "Burn subtitles" (ffmpeg, uses a sibling .srt if present) | high | S |
| 4 | Clean up voice (denoise, de-reverb) | Adobe Podcast Enhance: free capped at 1 h/day and 30 min files, Premium about $9.99/mo or with Creative Cloud [12]; Descript Studio Sound on paid tiers [10] | DeepFilterNet 3 (MIT / Apache-2.0); ffmpeg `arnndn` as a lightweight fallback | DeepFilterNet runs faster than real time on CPU alone; GPU not needed | Ctrl wheel, Audio and Video: "Clean up voice", single drop; video keeps the picture stream (copy) and replaces audio | high | S |
| 5 | AI image upscale 2x / 4x | Topaz Gigapixel about $149/yr, subscription-only since Sept 2025 (aggregator) [13]; Topaz Photo AI about $199/yr [13]; Luminar Neo $99/yr or lifetime about $199 [14] | Real-ESRGAN (BSD-3-Clause) via realesrgan-ncnn-vulkan (MIT) or ONNX; realesr-animevideov3 for drawings. Avoid copying Upscayl code (AGPL) | 4x of a 1080p photo about 1 to 4 s on the 4060; tile size keeps VRAM under 4 GB | Ctrl wheel, Image: "Upscale 2x", "Upscale 4x", single drop | high | S |
| 6 | NVENC hardware encoding for every video job | CloudConvert bills per conversion minute, about $8 per 500 min (aggregator) [11]; Permute markets hardware-accelerated encoding in its $14.99 license [15] | ffmpeg `h264_nvenc` / `hevc_nvenc` / `av1_nvenc` (RTX 40 has AV1 encode) and `-hwaccel cuda` decode | Typically 5 to 10x faster than x264 medium for 1080p; frees the CPU for parallel batches | Invisible: engine picks NVENC when `peel doctor` finds it, with a Settings toggle "Prefer quality (CPU)". Affects Compress, Convert, Speed, Join | high | S |
| 7 | Shelf / stash plus recent-drops history | Dropover Pro $4.99 one-time to remove a 3 s nag after the trial [16][17]; Yoink about $7.99 [18] | Native WPF window, no dependency | Trivial | Alt wheel: "Stash" drops files onto a tray-anchored shelf that collects across folders and apps; drag all out at once; shelf keeps the last N drops as a file history (Windows clipboard history does not keep files). Extends the planned Float action | high | M |
| 8 | Watched folders and saved recipes | Hazel $42 [19]; ABBYY Corporate HotFolder about $165/yr [7]; iLovePDF Workflows Premium-only [6]; Dropover folder observing and Instant Actions [20] | .NET FileSystemWatcher + the engine CLI; recipe = list of (action id, params) | Trivial compute; risk is only debounce and partial writes | From any wheel: long-press a segment to "Save as recipe" (action plus current params). Settings: map a folder to a recipe, e.g. Downloads/*.heic to JPG, Scans/*.pdf to OCR. Recipes also appear as wheel segments | high | M |
| 9 | True PDF redaction, including by pattern | Acrobat Pro only, Standard lacks it [5]; iLovePDF Smart Redact uses AI credits on Premium [6] | PyMuPDF `add_redact_annot` + `apply_redactions` removes underlying text and images (AGPL); regex presets for email, phone, RFC, CURP, CLABE, card numbers | CPU, instant | Ctrl wheel, PDF: "Redact" opens an editor (draw boxes, or tick patterns), writes `<name> (redacted).pdf`, then verifies by re-extracting text that no match remains | high | M |
| 10 | Erase object / watermark from image | Google Photos Magic Eraser needs Google One (from $1.99/mo) on non-Pixel phones [21]; Adobe Express Erase is Premium [3] | LaMa inpainting (Apache-2.0), packaged in IOPaint (Apache-2.0) or as ONNX | Under 1 s per 1080p image on the 4060, about 1 GB VRAM | Ctrl wheel, Image: "Erase", editor reuses the Redact brush to paint a mask, then inpaints | high | M |
| 11 | Translate documents and subtitles | DeepL Team about $350/user/yr with 20 file translations/mo (reseller) [22]; iLovePDF Translate is Premium-only [6] | Argos Translate (MIT, OPUS-MT based models); Helsinki OPUS-MT via CTranslate2 (CC-BY-4.0 weights). NLLB-200 is better but its weights are CC-BY-NC 4.0 (**NC**) [23] | 600M to 1.3B models run fast in int8 on the 4060; a 20-page DOCX in well under a minute | Shift wheel, DOCX/TXT/MD/SRT/VTT: "Translate to Spanish / English" (languages set in Settings). PDF goes PDF to DOCX then translate. SRT keeps timings | high | M |
| 12 | Separate stems (vocals, drums, bass, other) | LALAL.AI Lite about $7.50/mo, Pro about $15/mo, free tier allows no downloads (aggregators) [24] | Demucs v4 htdemucs (MIT) | About 10 to 30 s per song on the 4060, 3 to 4 GB VRAM; CPU several minutes | Ctrl wheel, Audio and Video: "Split stems" (4 files in a folder) and "Remove vocals" (karaoke, one file) | med | S |
| 13 | Password-protected archives (AES-256) | WinZip Pro about $42.95 one-time [25]; WinRAR $29 single user [26] | 7-Zip (LGPL-2.1, unRAR clause) already a backend; `-p -mhe=on` for 7z, AES-256 for zip | Instant | Shift wheel, ZIP/7Z targets get an optional "with password" variant that prompts once; Ctrl wheel, Archive: "Encrypt" | med | S |
| 14 | PDF protect, unlock, repair, PDF/A | iLovePDF: PDF/A not on Free, Repair batch limited on Free [6]; Acrobat Pro [5] | qpdf (Apache-2.0) for encrypt/decrypt/repair; ocrmypdf `--output-type pdfa` (Ghostscript, **AGPL**) for PDF/A | Instant to seconds | Ctrl wheel, PDF: "Protect", "Unlock" (prompts for password), "Repair". Shift wheel, PDF: "PDF/A" | med | S |
| 15 | Compress to a target size, lossy PNG | TinyPNG Web Pro about $39/yr (G2 listing) [27] | pngquant (GPL-3.0) or libimagequant; oxipng (MIT); mozjpeg (BSD); libavif. For video, 2-pass bitrate from target size | Instant for images; video is NVENC 2-pass | Ctrl wheel, Image/Video/PDF Compress gains presets "Under 1 MB", "Under 10 MB" (email, WhatsApp, Discord), searching quality until the size fits | med | S |
| 16 | Lossless instant trim and cut | LosslessCut is $19 on the app stores, free on GitHub [28] | ffmpeg stream copy (`-c copy`) at keyframes (LGPL/GPL build) | Instant, no re-encode | Planned Trim/Split editor gets a "Lossless (instant, cuts at keyframes)" toggle, on by default when cut points sit near keyframes | med | S |
| 17 | Read text aloud (text to speech) | Permute 4 bundles text-to-audio narration in its $14.99 license [15] | Kokoro-82M (Apache-2.0) or Piper (MIT; check each voice's license) | Kokoro is faster than real time on GPU and acceptable on CPU | Shift wheel, TXT/MD/DOCX/EPUB/PDF: new target MP3 / M4A ("Read aloud") | med | S |
| 18 | Compare two PDFs | Acrobat Pro only, Standard lacks Compare [5] | pypdfium2 (Apache-2.0 / BSD) render plus pixel diff; text diff with difflib; PyMuPDF (AGPL) to draw highlights | Seconds per page on CPU | Ctrl wheel, PDF, exactly 2 files dragged: "Compare", writes `<a> vs <b>.pdf` with changes highlighted and a summary page | med | M |
| 19 | Sign PDF (image signature and certificate signature) | Acrobat Standard/Pro [5]; PDF Expert $79.99/yr or $139.99 lifetime [29] | pyHanko (MIT) for PAdES digital signatures with a self-made or e.firma .p12/.cer+.key; image stamp via PyMuPDF | Instant | Ctrl wheel, PDF: "Sign" opens a page view to place a saved signature image; optional certificate sign. Signature images live in Settings | med | M |
| 20 | Cut silences and filler words | Descript: bulk filler removal and unlimited silence removal need paid tiers [30] | auto-editor (Unlicense) for silence cuts; faster-whisper word timestamps for "um/uh/este/o sea" | Fast; transcription dominates | Ctrl wheel, Audio and Video: "Cut silences", "Cut filler words" (uses item 3) | med | M |
| 21 | Summarize a document locally | Acrobat AI Assistant needs Studio, $24.99/mo [5]; iLovePDF AI Summarizer limits by plan [6] | llama.cpp (MIT) or Ollama (MIT) with Qwen2.5-7B-Instruct Q4 (Apache-2.0) | 7B Q4 fits in 8 GB VRAM at roughly 30 to 50 tokens/s on a 4060; context limits long PDFs to chunked map-reduce | Ctrl wheel, PDF/DOCX/TXT/MD/SRT: "Summarize" writes `<name> (summary).md`; pairs with transcription for meeting notes | med | M |
| 22 | AI video upscale and frame interpolation (60 fps, slow motion) | Topaz Video AI about $299/yr (aggregators) [13][31] | RIFE (MIT) via rife-ncnn-vulkan (MIT) or TensorRT; Real-ESRGAN anime/video models per frame; NVENC for encode | RIFE 1080p 2x roughly real time or better; 4x upscale is minutes per minute of 1080p video | Ctrl wheel, Video: "Smooth to 60 fps", "Slow motion 0.25x (AI)", "Upscale 2x"; progress toast with ETA, cancellable | med | L |
| 23 | Remove video background | Canva video background remover is Pro-only [2]; Adobe Express video background removal is Premium [3] | BiRefNet-lite per frame with temporal smoothing (MIT); Robust Video Matting (GPL-3.0) as alternative | A few fps at 1080p on the 4060; fine for short clips | Ctrl wheel, Video: "Remove background" writes WebM with alpha or a green-screen MP4 | med | L |
| 24 | Quick preview (spacebar on shelf or wheel) | Mac Quick Look is free; Windows baseline is PowerToys Peek (free) | WebView2 / WPF MediaElement / PDFium thumbnails | Trivial | Space on a Stash item or a hovered Float shows a preview; low value since Peek exists | low | S |
| 25 | High quality GIF | Gifski is free on the Mac App Store [32] | gifski (**AGPL-3.0**) or ffmpeg `palettegen`/`paletteuse` (no AGPL) | Seconds | Planned GIF target: use ffmpeg two-pass palette by default, offer a quality slider | low | S |

Free baselines checked: PowerToys (Image Resizer, Text Extractor, Peek, File
Locksmith, Advanced Paste; still free as of 0.100, June 2026 [33]) and File
Converter (free, context-menu only, no AI). Neither offers background removal,
upscaling, transcription, voice cleanup, translation, PDF redaction or a shelf,
which is where Peel can beat both.

## Recommendation: build these first

Build the four single-drop AI tools first because each is a small runner on a
mature MIT/BSD/Apache model and replaces a recurring subscription: background
removal (1), OCR to searchable PDF and copy-text (2), transcription to SRT/TXT
(3), and voice cleanup (4). Add NVENC (6) in the same pass, since it is an
engine-level change that speeds up every video job Peel already plans and costs
almost nothing. Then ship image upscale (5) and the Stash shelf with drop
history (7), which together give the most visible "this is better than the Mac
app" moments for little code. Eighth is watched folders with saved recipes (8),
which turns every other runner into automation and is the main thing Hazel,
ABBYY Corporate and iLovePDF Workflows charge for. True PDF redaction with
Mexican ID patterns (9) and document translation (11) are the strongest next
wave; keep NLLB (NC), PyMuPDF and Ghostscript (AGPL) in mind if Peel is ever
shared beyond George.

## Sources

1. remove.bg pricing (aggregators): https://tekpon.com/software/removebg/pricing/ , https://aitoolsatlas.ai/tools/remove-bg/pricing
2. Canva background remover (Pro): https://canva.com/pro/background-remover , https://www.canva.com/en_in/pro/video-background-remover
3. Adobe Express remove background: https://www.slashgear.com/1784748/adobe-express-remove-background-feature-how-use , https://www.adobe.com/express/feature/video/remove-background
4. BiRefNet MIT license: https://www.promptlayer.com/models/birefnet , https://arxiv.org/pdf/2401.03407
5. Acrobat Standard vs Pro vs Studio: https://xodo.com/blog/adobe-acrobat-pricing-explained , https://www.pcworld.com/article/3433219/adobe-acrobat-standard-dc-vs-adobe-acrobat-pro-dc.html
6. iLovePDF pricing (Premium-only tools and limits): https://www.ilovepdf.com/pricing
7. ABBYY FineReader PDF pricing: https://www.softwareadvice.com/cms/abbyy-finereader-profile/ , https://getpulsesignal.com/pricing/abbyy
8. CleanShot X and Shottr pricing: https://toolradar.com/tools/cleanshot/pricing , https://www.screensnap.pro/blog/shottr-mac-review
9. Otter.ai pricing (competitor-written): https://sonix.ai/resources/otter-ai-pricing/
10. Descript pricing (competitor-written): https://sonix.ai/resources/descript-pricing/
11. CloudConvert pricing: https://cloudconvert.com/pricing , https://www.getapp.com.au/software/121895/cloudconvert
12. Adobe Podcast limits: https://gauravtiwari.org/adobe-podcast-review/ , https://www.feisworld.com/blog/adobe-podcast-enhance-speech-v2
13. Topaz pricing (aggregators): https://www.renderahouse.com/blog/topaz-ai-pricing , https://www.myarchitectai.com/blog/topaz-ai-pricing
14. Luminar Neo pricing: https://photutorial.com/luminar-pricing
15. Permute 4: https://apps.apple.com/us/app/permute-4/id6774079417?mt=12 , https://toolradar.com/tools/permute-4-0
16. Dropover App Store listing (Pro $4.99 IAP): https://apps.apple.com/app/id1355679052
17. Dropover FAQ (one-time, removes 3 s delay): https://dropoverapp.com/faq
18. Yoink price: https://blog.eternalstorms.at/tag/drag-and-drop/
19. Hazel: https://www.macworld.com/article/632990/hazel-review-watches-folders-and-takes-automatic-action.html
20. Dropover Instant Actions and Cloud: https://dropoverapp.com/kb/instant-actions , https://dropoverapp.com/services/dropover-cloud
21. Magic Eraser via Google One: https://techcrunch.com/2023/02/23/google-photos-ai-powered-magic-eraser-is-now-a-google-one-subscription-perk/
22. DeepL plans (reseller): https://www.xlsoft.com/en/products/deepl/plan.html
23. NLLB license: https://github.com/facebookresearch/fairseq/blob/nllb/README.md
24. LALAL.AI pricing (aggregators): https://aitoolsatlas.ai/tools/lalal-ai/pricing , https://toolradar.com/tools/lalal-ai/pricing
25. WinZip Pro: https://www.trustradius.com/products/winzip/pricing
26. WinRAR: https://rarlab.com/buysharewarecd.php
27. TinyPNG Web Pro: https://www.g2.com/de/products/tinypng/software_advice_form_fill/new , https://getpulsesignal.com/pricing/tinypng
28. LosslessCut store pricing: https://mifi.no/blog/losslesscut-now-on-the-mac-app-store-and-microsoft-store/ , https://sixcolors.com/post/2026/01/trim-video-fast-with-lossless-cut/
29. PDF Expert pricing: https://toolradar.com/tools/pdf-expert/pricing
30. Descript filler and silence removal: https://www.descript.com/blog/article/best-silence-remover-tools , https://cleanvoice.ai/blog/descript-pricing-comparision/
31. Topaz Video: https://costbench.com/software/ai-video-generators/topaz-video-ai/
32. Gifski free: https://www.appgefahren.de/gifski-mac-app-videos-gifs-219223.html
33. PowerToys releases: https://devblogs.microsoft.com/commandline/
