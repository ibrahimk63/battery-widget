# BatteryWidget Anonim Bildirim Sunucusu Kurulumu (Ücretsiz Cloudflare Worker)

Kullanıcıların GitHub hesabı açmadan veya kopyala-yapıştır yapmadan desteklenmeyen USB `VID:PID` bilgilerini size ulaştırması için ücretsiz Cloudflare Workers kullanabilirsiniz (günlük 100.000 ücretsiz istek).

## 5 Adımda Kurulum

1. **Cloudflare Hesabı Açın:** [dash.cloudflare.com](https://dash.cloudflare.com/) üzerinden ücretsiz hesap oluşturun ve sol menüden **Workers & Pages → Create → Create Worker** yolunu izleyin.
2. **Kodu Yapıştırın:** `Deploy` sonrası **Edit code** ekranına geçin ve aynı klasördeki `worker.js` dosyasının içeriğini yapıştırıp **Deploy** deyin.
3. **Discord Webhook Ekleyin:**
   - Özel Discord sunucunuzda bir kanal açın → **Kanalı Düzenle → Entegrasyonlar → Webhook'lar → Yeni Webhook** ile URL'i kopyalayın.
   - Cloudflare Worker sayfasında **Settings → Variables and Secrets → Add** deyip `Secret` türünde adını `DISCORD_WEBHOOK_URL`, değerini ise Discord Webhook URL'i yapın ve kaydedin. *(Webhook asla `.exe` veya GitHub reposu içine yazılmaz.)*
4. **Worker Adresini Aktifleştirin:** Worker'ın `https://...workers.dev` adresini kopyalayın ve repo kökündeki [`profiles-db.json`](../../profiles-db.json) dosyasında `"reportEndpointUrl": "https://..."` alanına yazıp commit & push yapın.
5. **Bitti:** Kullanıcıların **BatteryWidget** uygulaması 24 saat içinde otomatik senkronizasyonla (yeni `.exe` indirmeden) tek tıkla anonim bildirim göndermeye başlar.
