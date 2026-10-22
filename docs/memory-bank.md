# TutorArchier — Memory Bank

Son güncelleme: 2026-10-22 (uçtan uca deneme, sessiz çoğunluk hatası, GitHub Pages, site güncellemesi). Her önemli adımdan sonra güncellenir; yeni bir oturum buradan devam eder.

## Ürün

Windows masaüstü uygulaması (WPF, .NET 10, WPF-UI 4.3). Eğitim/kurs video klasörlerini analiz eder ve bölümlü
(chapter), altyazılı tek bir MKV'de birleştirir. Orijinal dosyalara asla yazılmaz. Arayüz Türkçe (tr-TR).

Orijinal plan dokümanı ("plan §6.x", "Ek A" atıfları) repoda **yok**. Kapsam kod içi ipuçlarından çıkarıldı.

**Birincil kullanıcı geliştiricinin kendisi** (2026-10-22): uygulama önce kendi indirilmiş kurs arşivi için yapılıyor,
geliştirme makinesi asıl hedef makine. Public release ve site ikinci planda. Öncelik sırası: kendi kurslarında doğru
sonuç (içerik kaybı en kötü hata sınıfı), kendi donanımında hız, en son yabancı kullanıcılar için cila.

## Fazlar

| Faz | İçerik | Durum |
|---|---|---|
| 0 · Temel | ffmpeg bulma + otomatik kurma, encoder testi, hız ölçümü, ayarlar, onboarding, Fluent arayüz | ✅ |
| 1 · Analiz | Klasör tarama, ffprobe (+ `probe_cache`), altyazı–video eşleştirme ve dil tespiti, istatistik, sorunlu dosyalar | ✅ |
| 2 · Birleştir + Kuyruk | Birleştirme planı (kopyala/remux/ses/video kodla), chapter, altyazı kaydırma, SQLite kuyruk, duraklat/devam | ✅ |
| 3 · Arşiv modu | Birleştir'de "Sıkıştırarak arşivle": HEVC 10-bit, VMAF ile kalite araması, boyut tahmini (kapsam kullanıcıyla 2026-10-10'da seçildi) | ✅ |

## Kod haritası

- `src/TutorArchier.Core` — modeller ve arayüzler (`Media/`, `Jobs/`, `Hardware/`, `Settings/`), `AppPaths`
- `src/TutorArchier.Data` — SQLite (`AppDatabase` migration listesi: sadece sona ekle), store'lar
- `src/TutorArchier.Engine`
  - `Ffmpeg/` runner, locator, installer, progress parser, child-process job
  - `Hardware/` encoder katalog/test/benchmark, `HardwareService` (ffmpeg + rapor sahibi)
  - `Media/` `FolderScanner`, `MediaProber`, `SubtitleFiles`, `FolderAnalyzer`
  - `Merge/` `MergePlan` + `MergePlanner`, `MergeRunner`, `JobScheduler`, `QualitySearch` (arşiv modu VMAF araması)
- `src/TutorArchier.UI` — WPF; `Themes/` token + stiller, `ViewModels/`, `Views/Pages/`
  - `Services/WorkspaceState` seçili klasörü ve analizini tutar; Ana sayfa, Analiz ve Birleştir paylaşır
  - Sayfalar singleton; her gösterimde `Loaded` → `OnShownAsync()` (analiz yoksa başlatır)
  - `TutorArchier.exe "<klasör>"` klasör seçili açılır (test ve "Birlikte aç" için)
- `tests/TutorArchier.Tests` — xUnit; `Integration/` ffmpeg gerektirir (`[FfmpegFact]`)

## Kararlar ve nedenleri

- **ffmpeg = Gyan 8.1 essentials, sabit** (`FfmpegInstaller`: sürüm + boyut + SHA-256). 8.1.2+ ve 9.x NVENC API 13.1
  ister (sürücü 610+); Pascal (GTX 10xx) R580'de kaldı → NVENC çalışmaz. Sürümü yükseltmeden önce Pascal'da NVENC'i
  yeniden test et. `FfmpegInfo.TestedVersion` = 8.1; farklı sürüm bulunursa Ayarlar "Otomatik kur" önerir.
- **Kurulum**: SHA-256 doğrulama, `.partial` → rename. Hedef önce `<uygulama>\ffmpeg`, yazılamazsa `%LOCALAPPDATA%\TutorArchier\ffmpeg`.
- **Tek exe + gömülü ffmpeg** (kullanıcı isteği, 2026-10-13): `publish.ps1` Gyan zip'ini olduğu gibi Engine
  derlemesine `EmbeddedResource` (`ffmpeg.zip`, `-p:FfmpegBundle`) olarak gömer. Böylece koddaki sabit SHA-256 gömülü
  kopyayı da doğrular. Sürüm ve SHA `FfmpegInstaller.cs`'ten regex ile okunur, tek kaynak orası.
  `HardwareService.LocateAsync` önce `EnsureBundledAsync` çağırır: ilk açılışta ve ffmpeg sürümü değişince
  `%LOCALAPPDATA%\TutorArchier\ffmpeg`'e açar (exe'nin yanına değil; işaret dosyası `bundled-version.txt` en son
  yazılır). "Otomatik kur" gömülü sürümde indirmez. Debug build'ler gömmez, indirme akışı orada sürer.
  `EnableCompressionInSingleFile` kapalı kalmalı: açılırsa derlemeler (gömülü zip dahil) her açılışta belleğe açılır.
  Exe içinde gereksiz `ffplay` (~36 MB) var (ponytail notu). Release notu FFmpeg GPLv3 bildirimi içerir, çünkü
  binary dağıtıyoruz.
- **Release**: `.github/workflows/release.yml`. `v*` tag push → `publish.ps1 -Version <tag>` → `gh release create`
  (otomatik notların başına indirme + GPL notu eklenir). Elle "Run workflow" release açmaz, exe'yi artifact yapar.
  Aynı tag'i yeniden pushlamak "release already exists" ile düşer; v0.2.0'da öyle oldu, release ilk koşudan duruyor.
- **Web sitesi** (kullanıcı isteği, 2026-10-22): `site/` (tek `index.html`, satır içi CSS, `shots/` ekran görüntüleri)
  → `.github/workflows/pages.yml` ile GitHub Pages. Yalnızca `site/` yayımlanır, çünkü `docs/` iç notlar. İndirme
  düğmesi `releases/latest`'e gider, sürüm numarası sitede yok (her release'te dokunmamak için). Ekran görüntüleri
  demo kurstan; hedef format ekranı değişirse yeniden çek.
- **Birleştirme hedefi**: süreye göre çoğunluk formatı. `FormatSignature` = video codec, çözünürlük, pix_fmt, video
  time base, ses codec/örnekleme/kanal, ses time base.
- **Concat demuxer tuzakları**: (1) Tüm dosyaları ilk dosyanın time base'inde okur → hazırlanan segmentler hedef time
  base'de yazılır (MP4 `-video_track_timescale N` veya MKV 1/1000; üretilemiyorsa hepsi MKV'ye remux). (2) Akışları
  index ile eşler → layout video 0, ses 1 şart, değilse remux. (3) H.264 parametre setleri `auto_convert` ile in-band;
  HEVC için yok (ponytail notu `MergeRunner.ConcatList`).
- **Şeffaf kalite** (kayıpsız modda uyumsuz dersler için): NVENC CQ 19, AMF QP 18, x264 CRF 17, x265 CRF 18. Pascal HEVC NVENC'te
  B-frame yok; `EncoderCapability.BFrames` makine başına ölçülür.
- **Arşiv modu** (`MergePlanner.Create(..., archiveQuality)`): hedef HEVC `yuv420p10le` (donanımda `p010le`),
  çözünürlük ve ses çoğunluktan. **Her ders kodlanır**, zaten HEVC 10-bit olanlar da: birleşik MKV başlığına yalnızca ilk
  parçanın parametre setleri girer, başka encoder'ın HEVC'si bozuk çözülebilir. Parçalar **MP4, 90 kHz**: kayıpsız
  yoldaki gibi, AAC encoder gecikmesi edit list'te kalır. MKV'de video 21 ms kayıyor, demo çıktısında çözme uyarıları
  çıktı; MP4 ile temiz.
- **VMAF araması** (`QualitySearch`): kursa yayılmış 4 örnek × 5 sn (dersin 1/3'ünden). Aralık `[şeffaf, şeffaf+16]`
  (`EncoderPresets.ArchiveRange`) içinde ikili arama, ≤5 deneme, hedef VMAF 95. Kareler **sıra numarasıyla eşlenir**
  (`settb=AVTB,setpts=N`): örneğin 1/1000 time base'i 33,37 ms'yi 33'e yuvarlıyor, zaman damgasıyla eşleyince her kare
  bir öncekine kıyaslanıyordu (VMAF 73 yerine 97). Ses örneğe kopyalanır, boyut tahmini sesi de kapsar. libvmaf şart.
  Kurs skoru = `min(ortalama, en kötü + 2)` (`QualitySearch.Aggregate`): ortalamada şeffaf olmalı ve hiçbir kesit 2
  puandan fazla geride kalmamalı. Böylece statik slaytlar yumuşak çıkan tek kamera kesitini bastıramaz.
  Arama kuyrukta değil, Birleştir ekranında çalışır: kullanıcı tahmini boyutu ekleme öncesi görür.
- **Her alt klasör ayrı video** (kullanıcı isteği, 2026-10-10): `MergePlanner.Sections` analizi üst düzey alt
  klasörlere böler; daha derin klasörler o videonun içinde kalır ve bölüm adında görünür. Kök klasördeki videolar kurs
  adıyla ayrı bir video olur. Yollar alt klasöre göre yeniden yazılır, yani bölüm adı "01 Giriş › x" değil "x".
  `CreatePerSubfolder` her biri için ayrı plan üretir; her plan kuyrukta ayrı iş. Çıktı `<Kurs> (MKV)\<alt klasör>.mkv`,
  MKV başlığı ve iş adı `Kurs › Alt klasör`. Alt klasör varsa (≥2 grup) varsayılan açık; Birleştir'deki anahtarla tek
  videoya dönülür. Format ve kodlama kararları her video için ayrı; arşiv kalitesi kurs başına tek arama.
  `MergeViewModel` her zaman `Plans` listesiyle çalışır, tek video = 1 elemanlı liste.
- **Ek dosyalar (resource zip)** (kullanıcı isteği, 2026-10-14): `FolderScanner` medya olmayan her dosyayı `OtherFiles`'a
  toplar. `MergePlanner.Bundle` = OtherFiles + eşleşmeyen/desteklenmeyen altyazılar + okunamayan videolar ve
  altyazıları; hedef `<çıktı klasörü>\resource\<Kurs>.zip`, kök kurs klasörü (yapı korunur). Plan alanı `Resources`,
  ayrı bir adım; alt klasör modunda yalnızca ilk planın işinde. Zip önce iş klasöründe `.partial` yazılır (iptal
  temizler), sonra overwrite:false ile taşınır. Sıkışık uzantılar `NoCompression`, gerisi `Fastest`.
  Birleştir'de "Ek dosyaları zip'le", ek dosya varsa varsayılan açık.
- **MP4 çıktı** (kullanıcı isteği, 2026-10-14): Biçim çıktı uzantısından (`MergePlanner.IsMp4`); plan alanı yok, eski
  kuyruk işleri MKV kalır. MP4'te hedef video `h264/hevc/av1/vp9/mpeg4` dışındaysa H.264, ses `Mp4Audio` dışındaysa AAC.
  Birleştirmede `-c:s mov_text`, HEVC'de `-tag:v hvc1`, altyazı adı `handler_name`. Chapter'lar ffmetadata'dan MP4'e de
  yazılıyor. `-movflags +faststart` yok (dosyayı ikinci kez yazar; yerel oynatmada gereksiz). Birleştir'de "MP4 olarak
  kaydet"; değişince çıktı adı varsayılana döner. Alt klasör modunda klasör `<Kurs> (MP4)`.
- **Paralel adımlar** (2026-10-14): `MergeRunner` hazırlık adımlarını üç kulvarda çalıştırır: video kodlama
  (donanım 2, yazılım 1 slot; x264/x265 zaten tüm çekirdekleri kullanıyor), hafif adımlar (remux/ses, 2 slot) ve
  zip. Birinin hatası diğerlerini iptal eder. Adımlar sırasız bittiği için devam etmek `completedSteps`'e değil
  diskteki dosyaya bakar (`.partial`'dan çıkan = bitti); `RunAsync` artık `completedSteps` almıyor.
  `onStepCompleted`/`onProgress` kilit altında, aynı anda çağrılmaz. `MergeProgress` = (Fraction, Text); Text
  çalışan adımları " · " ile birleştirir. `QualitySearch` örnekleri 2'şer ölçer (biri kodlarken diğeri VMAF),
  libvmaf `n_threads` = çekirdek / 2. İşler kuyrukta hâlâ tek tek.
- **Concat + AAC**: Ses tam AAC çerçevesine denk gelmezse sınırlarda <1 ms "non monotonically increasing dts" uyarısı
  çıkar, düz kayıpsız concat'te bile. Zararsız; hata sanıp kovalama.
- **Altyazı**: SRT/VTT okunur, SRT yazılır. UTF-8 değilse Windows-1254. Dil dosya adından (`.en`, `_tr`, `[Turkish]`).
  Dil başına tek track; o dilde altyazısı olmayan derslerde boşluk; ders sonunu aşan cue kesilir.
- **Kuyruk**: `jobs` tablosu (plan JSON + `completed_steps`). Adım = her hazırlanan segment + son birleştirme.
  Uygulama kapanınca yarıda kalan iş açılışta kaldığı adımdan otomatik sürer. Ara dosyalar çıktının yanında
  `<çıktı>.tutorarchier-parts`.
- **Arayüz**: Tek palet `Tokens.Dark/Light.xaml` (anahtarlar iki dosyada aynı olmalı). WPF-UI fırçaları token
  sözlüğünde eziliyor — `StaticResource` ile çözülen buton yazı rengi vb. dahil. **`SymbolIcon`: kod noktası U+FFFF'in
  üstündeki semboller çizilmez.** WPF-UI 4.3 değeri tek `char`'a kesiyor: `FolderSearch24` U+F038F → "Ώ",
  `FolderMultiple24` U+F0ADF → boş. Yeni ikonun değerini `(int)SymbolRegular.X` ile kontrol et, ≤ 0xFFFF olmalı. WPF `CornerRadius` yarı yükseklikten büyükse elips olur.
  Tablolar için `TableList` stili kullan: WPF-UI 4 standart `ListView`'e stil vermiyor (beyaz zemin, sıkışık satırlar).
- **Arayüz düzeni ("Zengin Fluent", kullanıcı seçti, 2026-10-10)**:
  - **Ekranlar:** Analiz/Birleştir/Ana sayfa üstünde `Controls/CourseHeader` (2×2 kapak mozaiği + ad + özet + yol;
    DataContext = `WorkspaceState`). Birleştir'de "Seçenekler" paneli (ayrı video / arşiv / kayıt yeri) ve her zaman
    görünen alt eylem çubuğu (`FooterTitle`/`FooterDetail` + Kuyruğa ekle). Tablolarda küçük resim.
  - **Ana sayfa:** "Son açılanlar". `RecentFolder` ayarı en fazla 8 kurs tutar; analiz bitince `WorkspaceState.Remember`
    ile yazılır. Kart tıklanınca o kurs Birleştir'de açılır.
  - **Küçük resim:** `Engine/Media/ThumbnailCache` dersin %25'inden bir kare alır (`-noaccurate_seek`, 320 px JPEG,
    `%LOCALAPPDATA%\TutorArchier\thumbs`, anahtar = yol + boyut + değişim zamanı, aynı anda en fazla 3).
    `Controls/VideoThumb` resmi Border arka planı olarak (`ImageBrush`) çizer, çünkü köşe yuvarlaması `Image`'ı kırpmaz.
    `VideoThumb.Loader` App'te bağlanır.
  - **Kuyruk:** Menüde rozet (`InfoBadge`, çalışan + sıradaki iş sayısı) ve görev çubuğu düğmesinde ilerleme
    (`TaskbarItemInfo`). `Services/JobNotifier` pencere önde değilken iş bitince veya hata verince Windows bildirimi
    gösterir (`Hardcodet.NotifyIcon.Wpf` balon). Bu paket tüm WindowsDesktop framework'ünü çekiyor, zip 63 → 73 MB.
    Küçültmek gerekirse kendi `Shell_NotifyIcon` interop'u yazılır.
  - **Klasör argümanıyla açılış:** Analiz ffmpeg bulunmadan istenirse `WorkspaceState` ffmpeg bulununca bir kez
    yeniden dener (`RetryOnceFfmpegFound`).
- **Kaydırma**: WPF-UI, `Page`'lerde `ScrollViewer.CanContentScroll` varsayılanını `true` yapar ve `true` ise sayfayı
  kendi `DynamicScrollViewer`'ına sarar. Bizim sayfa `ScrollViewer`'ı o zaman sonsuz yükseklik alıp tekerleği yutuyordu.
  Her `Page`'de `ScrollViewer.CanContentScroll="False"` şart (yeni sayfada unutma). Ayrıca `App.ChainMouseWheel`:
  kaydıramayan iç ScrollViewer (kısa tablo, sonuna gelmiş liste) tekerleği en yakın kaydırabilen dış ScrollViewer'a verir.
- **ViewModel ilk durum**: Sayfalar ilk ziyarette oluşur; `Workspace`/`Hardware` olayları ondan önce olmuş olabilir.
  Her ViewModel kurucuda mevcut durumu yüklemeli (Analiz sayfası, analiz Birleştir'den başladığında boş kalıyordu).
- **İkon**: `Assets/TutorArchier.ico` (16–256 px, başlık logosu: `Brush.Logo` gradyan + dolu `Archive24`). Scratchpad'deki
  tek seferlik WPF aracıyla `SymbolIcon` render edilip üretildi. Logo değişirse yeniden üret.

## Çalışma şekli

- Build: `dotnet build TutorArchier.slnx` — **0 uyarı** kuralı (CA analizörleri dahil)
- Test: `dotnet test tests/TutorArchier.Tests` — ffmpeg PATH'te yoksa entegrasyon testleri atlanır. Çalıştırmak için:
  `$env:PATH = "$PWD\src\TutorArchier.UI\bin\Debug\net10.0-windows\ffmpeg;$env:PATH"` (uygulamadan kurulmuş ffmpeg)
- Paket: `./publish.ps1 [-Version x.y.z]` → `artifacts/TutorArchier-<sürüm>-win-x64.exe` (self-contained tek exe, ffmpeg gömülü)
- Sürüm: `git tag v0.2.0; git push origin v0.2.0` → GitHub Actions release açar
- Uygulama verisi: `%LOCALAPPDATA%\TutorArchier` (`tutorarchier.db`, `logs\`)
- Kod stili: yorumlar İngilizce ve "neden" anlatır; arayüz metinleri Türkçe; `ponytail:` yorumları bilinçli kısayollar
- `README.md` (Türkçe, kullanıcıya dönük): özellik, altyazı adlandırma, çıktı yeri ya da ölçüm değişince güncelle

## Doğrulama durumu (2026-10-10)

- 60 test (Release'te ffmpeg PATH'te değilse 7'si atlanır). `MergeIntegrationTests` gerçek ffmpeg ile klip üretir, analiz →
  plan → yarıda kes → kaldığı yerden devam → MKV'yi ffprobe ile doğrular (chapter, `eng`/`tur`, süre, 1254 → UTF-8).
- Arayüz elle denendi: demo kurs (2 bölüm klasörü, 720p + 480p + sessiz + bozuk dosya, EN/TR/VTT/1254 altyazı) →
  Analiz → Birleştir → Kuyruk → doğru MKV, sorunsuz decode.
- Elle denenmedi: arayüzden Duraklat/Devam et ve uygulamayı iş ortasında kapatıp açma (motor tarafı testli). Demo işi
  NVENC ile 3 sn'de bittiği için duraklatmaya yetişilemedi.
- 68 test (Release paketlemede ffmpeg PATH'te olmadığı için 8'i atlanır). Arşiv modu: birim (plan, ikili arama, skor
  birleştirme) + entegrasyon (libx265 ile arama → plan → MKV doğrulama).
- Kullanıcı ilk paketi denedi (2026-10-10). Bildirdikleri: tekerlekle kaydırma yok, ikon yok, "çok eksik duruyor".
  Düzeltildi: kaydırma, ikon, boş Analiz sayfası. Ardından "her alt klasör ayrı video" eklendi: demo kursta
  `01 Giriş.mkv` (2 bölüm, eng+tur) ve `02 Temeller.mkv` (2 bölüm, tur) üretildi, ikisi de uyarısız çözülüyor. 69 test.
- Temiz build ve arayüz yenilemesi (2026-10-10 20:21): 70 test (küçük resim entegrasyon testi eklendi), 0 uyarı.
  Koyu ve açık temada tüm sayfalar ekran görüntüsüyle kontrol edildi. Rozet ve Windows bildirimi gerçek bir işle
  denendi. Paket SHA-256 `1319F1CDD5987C89…`.
  Demo kurs arayüzden: arama 51 sn (CQ 27/31/33/32 denendi, seçilen CQ 31, VMAF 95,3); çıktı 21,3 MB, kayıpsız 64,4 MB
  (−%67), tahmin 20 MB; HEVC Main 10, 4 bölüm, `eng`/`tur`, uyarısız çözülüyor.
- Açıklanamayan: test sırasında uygulama iki kez açılıştan birkaç sn sonra exit 0 ile kapandı. Log'da ve olay
  günlüğünde hata yok; ikisi de önceki örnek `Stop-Process -Force` ile öldürüldükten hemen sonraydı. Tekrar
  üretilemedi, izlenmeli.
- Tek exe (2026-10-13): `publish.ps1 -Version 0.2.0-local` → 280 MB exe, 61 test geçti / 9 atlandı, 0 uyarı.
  `%LOCALAPPDATA%\TutorArchier` yokken açıldı: ffmpeg 8.1 açıldı ve kullanıldı, onboarding doğrudan NVENC testine
  geçti. İkinci açılışta tekrar açılmadı. GitHub Actions workflow'u henüz gerçek bir tag ile çalıştırılmadı.

- Resource zip + paralel adımlar (2026-10-14): 71 test, 0 uyarı (entegrasyon: arşiv modunda 2 kodlama + zip +
  birleştirme, adım sayacı sırayla 1..4, zip içeriği). Birleştir ekranı gerçek kursla görüldü (7 dosya, 65 MB →
  `resource\Asp.NET MVC Eğitimi.zip`). NVENC ile 2 paralel kodlamanın gerçek hız kazancı henüz ölçülmedi.
- MP4 çıktı (2026-10-14): 73 test, 0 uyarı. Entegrasyon: iki alt klasör → tek MP4, chapter "01 Giriş › 01 a",
  `mov_text` + `tur`, altyazı 1,5 sn kaydırılmış. MP4 seçeneği arayüzde henüz elle denenmedi.
- GitHub'da exe çıkmamasının nedeni: repoda hiç `v*` tag'i yoktu, workflow yalnızca tag push'unda (ya da Actions'tan
  elle "Run workflow") çalışıyor. Repo özel, API 404 veriyor; makinede `gh` kurulu değil.

- Uçtan uca deneme (2026-10-22): repo artık public, `v0.2.0` release'i exe ile yayında. `publish.ps1` → 280 MB exe,
  `%LOCALAPPDATA%\TutorArchier` yokken açıldı, gömülü ffmpeg 8.1 açıldı, onboarding NVENC'i buldu. 73/73 test
  (ffmpeg PATH'teyken entegrasyon dahil). Demo kurs klasör argümanıyla açıldı → 2 MKV + zip, chapter ve `eng`/`tur`
  doğru. **Hata bulundu:** bir alt klasörde sessiz dersler süreye göre çoğunluktaysa hedef format sessiz oluyor ve
  sesli derslerin sesi atılıyor (`02 Temeller.mkv`'de ses track'i yoktu). **Düzeltildi:** sessiz çoğunlukta ses
  formatı sesli derslerin kendi süre çoğunluğundan alınır, time base çoğunluğun kabına göre (MKV 1/1000, MP4
  1/örnekleme); sessizlere `anullsrc`. Test `A_silent_majority_does_not_drop_the_audio_of_the_others`, 74 test.
  Demo kursta yeniden denendi: `02 Temeller.mkv`'de AAC var, ilk ders -21 dB, sessiz ders -91 dB.
  Hız ölçümü demo üretimiyle aynı anda koştuğu için düşük çıktı (H.264 NVENC 168, HEVC NVENC 175 fps); x265 satırında
  hız "—" kaldı, nedenine bakılmadı.

## Açık işler (sıradaki)

0. Site yayında: https://mihrimatrix.github.io/tutor-archier/ (2026-10-22). Geçmiş tek commit'e indirilip
   force-push ediliyor (kullanıcı isteği); yeni kök commit'te `paths` filtreli Pages workflow'u tetiklenmiyor,
   `gh workflow run Pages` ile elle çalıştır.
   Site 2026-10-22 akşamı README ile eşitlendi (Analiz, MP4, sessiz ders, kuyruk bildirimi; `shots/analysis.png` yeni,
   `merge.png` yeniden çekildi). Kesilen satırın nedeni `StatusIndicator`'daki yatay `StackPanel`'di (sonsuz genişlik
   → `Wrap` çalışmaz); `DockPanel` yapıldı. İndirme düğmesi hâlâ v0.2.0'a gidiyor, sessiz çoğunluk düzeltmesi
   o exe'de yok: yeni bir tag gerekiyor.
   Paralel NVENC kodlamanın hızını gerçek kursla ölç (2 slot GP104'ün 2 NVENC motoruna göre seçildi).

1. Kullanıcının gerçek kurs testlerinden dönen bulgular (performans, Duraklat/Devam et, uygulama kapat-aç, arşiv
   modu kalitesi).
2. `TODO(human)`: `UiCulture.FormatAgo`, Son açılanlar kartındaki zaman ifadesi ("bugün", "dün", "3 gün önce"…).
   Şimdilik tam tarih gösteriyor.
3. Olası iyileştirmeler: ders başına kalite araması (ekran kaydı + kamera karışık kurslar); VMAF hedefi ayarı; aramayı
   kuyruğa taşımak (çok kurs art arda eklenirse); arama sürerken Video bölümü hâlâ kayıpsız planı gösteriyor; ASS/SSA
   altyazı; HEVC karışık-encoder klasörlerde (kayıpsız mod) `hevc_mp4toannexb`; menüde kuyruk rozeti; çalışan iş
   varken kapatırken bilgi; çıktı kaynak klasörün içine seçilirse uyarı

## Geliştirme makinesi

i7-6700K, GTX 1080 (Pascal), NVIDIA sürücü 582.78 (R580, Pascal için son seri). 1080p hız ölçümü (ffmpeg 8.1):
HEVC NVENC ~310 fps, H.264 NVENC ~214 fps, x265 ~19 fps, x264 ~29 fps.
