# BatteryWidget

Windows için kablosuz cihazlarınızın (2.4GHz USB dongle, Bluetooth ve oyun kolları) pil yüzdelerini ve şarj durumlarını **tek bir masaüstü widget'ında ve Sistem Tepsisinde (System Tray)** gösteren hafif bir C# / .NET WPF uygulamasıdır.

## Tek Tıkla İndir (Kurulumsuz)

Bilgisayarınızda .NET kurulu olmasına gerek yoktur; tek dosyayı indirip doğrudan çalıştırabilirsiniz:

- **[BatteryWidget.exe İndir (Windows x64 — Tek Dosya)](https://github.com/ibrahimk63/battery-widget/releases/latest/download/BatteryWidget.exe)**
- **[Tüm Sürümler & ZIP Paketi (GitHub Releases)](https://github.com/ibrahimk63/battery-widget/releases/latest)**

## Desteklenen Cihazlar ve Sağlayıcılar

- **HyperX Cloud III Wireless** (`0x03F0:0x05B7` / `0x0FCC` / `0x098D`) — 62-byte USB HID kontrol kanalı (`0x66 / 0x89` & `0x66 / 0x8A`)
- **Darmoshark M3 V2 & Realtek 2.4GHz** (`0x0BDA:0xFFE0` / `0xFFEA` ve `0xB5`/`0xB6` destekleyen tüm Realtek alıcıları)
- **Wraith Ekosistemi & Evrensel CompX/PAW3950 (`v1.0.5`)** — VXE R1 SE+ / R1+ / R1 Pro / Mad R (voltaj fallback dahil), WLMouse (`0x36A7` Beast X vb.), Lamzu, Scyrox, MCHOSE, Ninjutso, ATK, VGN, Monster Pusat Ghost Pro, Throne Repel 8K, Evision/NearLink 8K (`0x05`), Hechi 8K (`0x0B`) ve HFD kablosuz klavyeler
- **Logitech (HID++ 2.0)** — Lightspeed, Bolt ve Unifying alıcılı tüm Logitech kablosuz fare/klavye/kulaklıklar (`0x1004` / `0x1000`)
- **Razer Wireless** — 90-byte Feature Report protokolüyle kablosuz Razer fare, klavye ve kulaklıklar
- **SteelSeries & ATK N9 Serisi** — Arctis / Arctis Nova kulaklıklar (`0x1038`), ATK N9 Pro / Ultra (`9600-baud USB Serial` protokolü) ve ATK N9 V2 (`Bolutek HID 0x55/0x65`)
- **Bluetooth Cihazları (Classic + BLE)** — Hem Windows PnP (`DEVPKEY_Bluetooth_Battery` — Classic Bluetooth kulaklık, fare, klavye vb.) hem de BLE GATT Battery Service (`0x180F` / `0x2A19`) cihazlarını aktif bağlantı kontrolüyle çoklu olarak algılar
- **Oyun Kolları (Xbox & PlayStation)** — Xbox Kablosuz Dongle / USB / Bluetooth kolları (`XInput` + `Windows.Gaming.Input` — gerçek yüzde veya `Dolu`/`Orta`/`Düşük`/`Boş` kademe gösterimi) ve DualSense / DualShock 4
- **Standart USB/HID Pil Aygıtları** — Standart HID pil raporu sunan diğer harici kablosuz cihazları otomatik algılar (dizüstü bilgisayarın dahili pilini hariç tutar)
- **Evrensel HID Parmak İzi Motoru** — Markadan bağımsız olarak Rapoo / 8K fareler (`0x07` 19B Input), Hechi 8K (`0x0B`), Standart HID Input+Feature pil raporlarını descriptor parmak iziyle otomatik tanır
- **Cihaz Öğret (Auto-Learn Sihirbazı) & Cihaz Kaldırma/Yönetimi** — Kartlara sağ tıklayarak istenmeyen cihazları kaldırıp gizleyebilir, Ayarlar → Cihaz Yönetimi sekmesinden geri getirebilir ve öğrenilen profilleri silebilirsiniz

## Sıkça Sorulan Sorular

- **Farenin 1000Hz / 4000Hz / 8000Hz kullanması CPU kullanımını artırır mı?**
  Hayır. Fare hareket verisi ayrı bir USB kanalından akar; BatteryWidget hareket paketlerini dinlemez, yalnızca seçilen tarama aralığında (ör. 1 dakikada bir) ayrı kontrol kanalından tek bir pil sorgusu okur.
- **Genel Aygıt Tanılama**: Ayarlar → Aygıt Tanılama sekmesinden her bilgisayarın kendi bağlı USB HID, Report ID ve sanal COM portlarını tek tıkla tarama

## Özellikler

- **Kompakt 2'li Sayfalı Dashboard UI**: Ekranda her zaman en fazla 2 cihaz kartı gösterilir; 2'den fazla cihaz bağlandığında fare tekerleği (scroll) veya sayfa noktaları ile sayfalar arasında geçiş yapılır.
- **CPU Optimizasyonu**: 5 dakikaya kadar seçilebilir akıllı tarama adımları (`10 sn`, `20 sn`, `30 sn`, `1 dk`, `2 dk`, `3 dk`, `5 dk`), önbellekli HID descriptor taraması ve değişmeyen arayüzde sıfır yeniden çizim.
- **Sistem Tepsisi (System Tray)**: Özel pil simgesi, tooltip özeti ve hızlı menü.
- **Düşük Pil Bildirimi**: Belirlenen eşiğin (varsayılan `%15` veya `Düşük`/`Boş`) altına düşen cihazlar için spam önlemeli Windows bildirimi.
- **Windows Başlangıcı & Dahili HID Tanılama**: Ayarlardan tek tıkla başlangıç ayarı ve VID/PID paket testi.

## Derleme ve Test

```powershell
dotnet build BatteryWidget.csproj
dotnet test tests/BatteryWidget.Tests/BatteryWidget.Tests.csproj
```

Çalıştırmak için:

```powershell
dotnet run --project BatteryWidget.csproj
```
