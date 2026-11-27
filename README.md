# BatteryWidget

Windows için kablosuz cihazlarınızın (2.4GHz USB dongle ve Bluetooth) pil yüzdelerini ve şarj durumlarını **tek bir masaüstü widget'ında ve Sistem Tepsisinde (System Tray)** gösteren hafif bir C# / .NET WPF uygulamasıdır.

## Desteklenen Cihazlar ve Sağlayıcılar

- **HyperX Cloud III Wireless** (`0x03F0:0x05B7` / `0x0FCC` / `0x098D`) — 62-byte USB HID kontrol kanalı (`0x66 / 0x89` & `0x66 / 0x8A`)
- **Darmoshark M3 V2** (`0x0BDA:0xFFE0` 2.4GHz alıcı & `0x0BDA:0xFFEA` kablolu) — Realtek 64-byte HID (`0xB5 / 0x05`)
- **Bluetooth Cihazları (BLE / GATT)** — Standart GATT Battery Service (`0x180F` / `0x2A19`) sunan tüm Bluetooth cihazlarını otomatik algılar
- **Özel USB / 2.4GHz Profilleri (`profiles/*.json`)** — Kod yazmadan yeni cihazlar eklemek için JSON profil desteği (`profiles/_template.json.example`)

## Özellikler

- **Minimalist Modern Dashboard UI**: Yan yana yuvarlak pil halkalı (radial ring) kompakt koyu kartlar, büyük dijital yüzdeler, uyku modunda son bilinen pil yüzdesini koruma.
- **Sistem Tepsisi (System Tray)**: Özel pil simgesi, tooltip özeti ve hızlı menü.
- **Düşük Pil Bildirimi**: Belirlenen eşiğin (varsayılan `%15`) altına düşen cihazlar için spam önlemeli Windows bildirimi.
- **Windows Başlangıcı**: Ayarlardan tek tıkla Windows açılışında otomatik başlatma.
- **Dahili HID Tanılama & Paket Testi**: Yeni cihazların VID/PID ve rapor boyutlarını listeleme.

## Derleme ve Test

```powershell
dotnet build BatteryWidget.csproj
dotnet test tests/BatteryWidget.Tests/BatteryWidget.Tests.csproj
```

Çalıştırmak için:

```powershell
dotnet run --project BatteryWidget.csproj
```
