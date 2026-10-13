# TutorArchier

Ders ders inmiş bir video kursunu tek dosyada toplayan Windows uygulaması. Klasörü seçiyorsunuz, TutorArchier
dersleri inceliyor ve her biri bir bölüm (chapter) olacak şekilde altyazılı bir MKV'de birleştiriyor. İsterseniz
aynı işi sıkıştırarak yapıyor; kursu, gözle fark edilmeyecek kalitede çok daha küçük bir HEVC dosyasına çeviriyor.

Orijinal dosyalara hiçbir zaman yazılmaz. Arayüz Türkçedir.

```
Python Kursu\                         Python Kursu (MKV)\
├── 01 Giriş\                         ├── 01 Giriş.mkv       ← 2 bölüm, eng + tur altyazı
│   ├── 01 Kurulum.mp4                └── 02 Temeller.mkv    ← 2 bölüm, tur altyazı
│   ├── 01 Kurulum.en.srt
│   ├── 01 Kurulum.tr.srt
│   ├── 02 İlk program.mp4
│   └── 02 İlk program_tr.vtt
└── 02 Temeller\
    ├── 01 Değişkenler.mp4
    ├── 01 Değişkenler [Turkish].srt
    └── 02 Döngüler.mkv
```

## Özellikler

- **Analiz:** Klasördeki tüm videoları ffprobe ile okur; toplam süreyi, boyutu, çözünürlük ve codec dağılımını,
  okunamayan ya da sesi olmayan dosyaları ve eşleşmeyen altyazıları gösterir. Sonuçlar önbelleğe alınır, aynı
  klasör ikinci kez çok daha hızlı açılır.
- **Kayıpsız birleştirme:** Dersler aynı formattaysa yeniden kodlanmadan birleşir; süre disk hızıyla sınırlıdır.
  Formatı farklı olan dersler yalnızca gerektiği kadar işlenir: önce yeniden paketleme denenir, gerekirse sadece
  ses, en son da görüntü kodlanır. Sesi olmayan derslere sessiz ses eklenir.
- **Bölümler:** Her ders, dosya adıyla bir chapter olur. Oynatıcıda derslerin arasında atlayabilirsiniz.
- **Altyazılar:** Aynı dildeki altyazılar tek bir track'te toplanır ve her dersin başlangıç anına kaydırılır.
  Altyazısı olmayan dersler o track'te boş geçer.
- **Her alt klasör ayrı video:** Kurs bölüm klasörlerine ayrılmışsa her klasör kendi videosu olur. Anahtar
  kapatılınca bütün kurs, alt klasörler dahil, tek bir bölümlü video olur (bölüm adı "01 Giriş › Ders 1").
- **MKV ya da MP4:** Varsayılan MKV. "MP4 olarak kaydet" ile telefon, TV ve tarayıcıların açtığı bölümlü bir MP4
  çıkar; altyazılar MP4'ün metin altyazısına (mov_text) çevrilir, MP4'e girmeyen ses/görüntü biçimleri dönüştürülür.
- **Sıkıştırarak arşivleme:** HEVC 10-bit'e kodlar. Kaliteyi VMAF ile ölçer ve farkın gözle seçilmediği en küçük
  boyutu seçer. Kuyruğa eklemeden önce tahmini boyutu görürsünüz. Bütün kursu (tüm alt klasörler) tek bir
  sıkıştırılmış videoya çevirmek için "Her alt klasör ayrı video"yu kapatıp arşivi açın.
- **Ek dosyalar:** Videoya girmeyen her şey (PDF, kaynak kod, görseller, okunamayan videolar, eşleşmeyen altyazılar)
  klasör yapısı korunarak `resource\<Kurs>.zip` içine konur. Zaten sıkışık dosyalar (zip, jpg, mp4…) sıkıştırılmadan
  eklenir, gerisi en hızlı ayarla; zipleme kodlamayla aynı anda çalışır.
- **Kuyruk:** İşler sırayla çalışır; bir işin içindeki adımlar paralel yürür (ekran kartıyla iki ders aynı anda
  kodlanır, yeniden paketleme ve zip yanında ilerler). Duraklatılan ya da uygulama kapanınca yarıda kalan iş, kaldığı
  dersten devam eder. İlerleme görev çubuğunda görünür, pencere arka plandayken iş bitince Windows bildirimi gelir.
- **Donanım hızlandırma:** NVIDIA (NVENC) ve AMD (AMF) ekran kartlarını kendisi test eder, çalışmayan bir şey varsa
  işlemciye (x264/x265) düşer.

## Gereksinimler

- Windows 10 veya 11, 64 bit
- Başka bir şey gerekmez: exe .NET çalışma ortamını ve ffmpeg 8.1'i içinde taşır. Kendi ffmpeg'inizi Ayarlar'dan
  yine de gösterebilirsiniz.

## Kurulum

1. [Releases](../../releases) sayfasından `TutorArchier-<sürüm>-win-x64.exe` dosyasını indirin (yaklaşık 300 MB).
2. İstediğiniz yere koyup çalıştırın. Windows SmartScreen uyarı verirse "Ek bilgi › Yine de çalıştır".
3. Karşılama ekranı ekran kartınızı test eder ve yaklaşık 30 saniyelik bir hız ölçümü yapar. İnternet gerekmez.

Kurulum yoktur. İlk açılışta ffmpeg `%LOCALAPPDATA%\TutorArchier\ffmpeg` klasörüne açılır; uygulama verisi de
`%LOCALAPPDATA%\TutorArchier` altındadır. Kaldırmak için exe'yi ve bu klasörü silmeniz yeterli.

Bu exe [FFmpeg](https://ffmpeg.org) içerir (gyan.dev "essentials" derlemesi, GPLv3 lisanslı; kaynaklar:
[gyan.dev/ffmpeg/builds](https://www.gyan.dev/ffmpeg/builds/)).

## Kullanım

1. **Klasör seçin.** Ana sayfadan, Analiz ya da Birleştir ekranından kurs klasörünü seçin. Klasörü exe'nin üzerine
   sürükleyebilir ya da komut satırından `TutorArchier.exe "D:\Kurslar\Python Kursu"` ile açabilirsiniz.
2. **Analize bakın.** Analiz ekranı sorunlu dosyaları ve eşleşmeyen altyazıları listeler. Okunamayan dosyalar
   birleştirmeye alınmaz, Birleştir ekranında hangilerinin atlandığı yazar.
3. **Seçenekleri ayarlayın.** Birleştir ekranında her alt klasörün ayrı video olup olmayacağını, arşiv modunu ve
   kayıt yerini seçin. Tablo her dersin bölüm adını, başlangıç anını ve ona ne yapılacağını gösterir (kopyala,
   yeniden paketle, ses kodla, video kodla).
4. **Kuyruğa ekleyin.** İşi Kuyruk ekranından izleyebilir, duraklatabilir, devam ettirebilirsiniz.

Varsayılan kayıt yeri kurs klasörünün yanıdır: tek video için `Python Kursu.mkv`, alt klasör modunda
`Python Kursu (MKV)\` klasörü. Bu ad doluysa `Python Kursu (2).mkv` ya da `Python Kursu (MKV 2)\` seçilir.
Ek dosyaların zip'i videoların yanındaki `resource\Python Kursu.zip` dosyasıdır.

Dersler Windows Gezgini'ndeki sırayla dizilir (`2 Ders` → `10 Ders`), yani numaralandırma için başa sıfır eklemeniz
gerekmez.

### Altyazı dosyaları

Altyazı, adı videonun adıyla başlıyorsa o videoya bağlanır. Dil dosya adının sonundan okunur:

| Dosya adı | Dil |
|---|---|
| `01 Kurulum.en.srt`, `01 Kurulum_en.srt` | İngilizce |
| `01 Kurulum.tr.vtt`, `01 Kurulum [Turkish].srt` | Türkçe |
| `01 Kurulum.srt` | Belirtilmemiş |

- SRT ve VTT okunur; çıktıda SRT olarak yer alır.
- UTF-8 olmayan dosyalar Windows-1254 (Türkçe) kabul edilip UTF-8'e çevrilir.
- ASS/SSA ve görüntü tabanlı altyazılar (SUB/IDX/SUP) analizde listelenir ama birleştirilmez.

### Arşiv modu

"Sıkıştırarak arşivle" açıldığında TutorArchier kursun farklı yerlerinden dört kısa kesit alır ve bunları farklı
kalite ayarlarıyla kodlayıp VMAF skorunu ölçer. Hedef, VMAF 95'in altına düşmeyen en küçük dosya. Yani
Kurs skoru kesitlerin ortalamasıdır, ama en kötü kesitin 2 puandan fazla altında olamaz. Böylece ekran kaydıyla
kamera görüntüsü karışık bir kursta, kolay sıkışan slaytlar zor sıkışan kamera kesitini gizleyemez. Arama kurs
başına bir kez yapılır.

Ölçülen örnek (GTX 1080): 64 MB'lık demo kursta arama 51 saniye sürdü, çıktı 21 MB oldu (%67 küçük); tahmin 20 MB'tı.

Arşiv modunda her ders yeniden kodlanır, bu yüzden kayıpsız birleştirmeden çok daha uzun sürer. İşlemciyle
kodlama (x265) ekran kartından çok daha yavaştır: geliştirme makinesinde 1080p'de HEVC NVENC ~310 fps, x265
~19 fps.

## Donanım notları

- **NVIDIA GTX 10xx (Pascal):** NVIDIA'nın bu kartlar için son sürücü serisi R580'dir. ffmpeg 8.1.2 ve sonrası daha
  yeni bir NVENC sürümü istediği için bu kartlarda çalışmaz; uygulamanın ffmpeg 8.1'e sabitlenmesinin nedeni
  budur. Pascal'da HEVC NVENC B-frame desteklemez, uygulama bunu kendisi algılar.
- **Ekran kartı yoksa ya da test başarısızsa** işlemci encoder'ları kullanılır. Sonuç aynı kalitede olur,
  yalnızca daha yavaştır.
- Ayarlar ekranında hangi encoder'ların çalıştığını ve hız ölçümünü görebilir, testi yeniden yapabilirsiniz.

## Geliştirme

Gerekenler: .NET 10 SDK, Windows.

```powershell
dotnet build TutorArchier.slnx          # 0 uyarı kuralı var, CA analizörleri dahil
dotnet test tests/TutorArchier.Tests    # ffmpeg PATH'te yoksa entegrasyon testleri atlanır
./publish.ps1                           # testler + artifacts/TutorArchier-<sürüm>-win-x64.exe (ffmpeg gömülü)
```

Yerel Debug build'lerde ffmpeg gömülü değildir; uygulama onu bugünkü gibi indirip kurar. `publish.ps1` sabitlenen
ffmpeg zip'ini `artifacts/`'a indirir, `FfmpegInstaller.cs`'teki SHA-256 ile doğrular ve exe'nin içine koyar.

**Sürüm çıkarmak:** `v` ile başlayan bir tag gönderin. GitHub Actions testleri çalıştırır, exe'yi üretir ve
release'i açar (sürüm numarası tag'den gelir):

```powershell
git tag v0.2.0
git push origin v0.2.0
```

Actions sekmesindeki "Run workflow" aynı exe'yi release açmadan indirilebilir dosya olarak üretir.

Entegrasyon testleri gerçek ffmpeg ile klip üretip birleştirir ve çıktıyı ffprobe ile doğrular. Uygulamanın
kurduğu ffmpeg ile çalıştırmak için:

```powershell
$env:PATH = "$PWD\src\TutorArchier.UI\bin\Debug\net10.0-windows\ffmpeg;$env:PATH"
dotnet test tests/TutorArchier.Tests
```

### Proje yapısı

| Proje | İçerik |
|---|---|
| `src/TutorArchier.Core` | Modeller ve arayüzler (medya, iş, donanım, ayarlar) |
| `src/TutorArchier.Data` | SQLite: ayarlar, ffprobe önbelleği, donanım raporu, iş kuyruğu |
| `src/TutorArchier.Engine` | ffmpeg çalıştırma ve kurulum, encoder testi, klasör analizi, birleştirme planı ve yürütme, VMAF araması |
| `src/TutorArchier.UI` | WPF arayüzü (WPF-UI 4.3, CommunityToolkit.Mvvm) |
| `tests/TutorArchier.Tests` | xUnit; `Integration/` klasörü ffmpeg gerektirir |

Tasarım kararları, bilinen tuzaklar (concat demuxer, time base, HEVC parametre setleri) ve açık işler
[docs/memory-bank.md](docs/memory-bank.md) dosyasında.

## Bilinen sınırlamalar

- Sadece Windows.
- ASS/SSA altyazılar birleştirilmez.
- Arşiv modunda kalite kurs başına tek ayarla seçilir; ders başına ayrı arama yok.
