# FileJobRouter

Basit bir dosya yönlendirme ve işleme sistemi. Ana uygulama açılır açılmaz `data/Test` dizinini izler, dosyayı uygun worker uygulamasına işler ve çıktıyı günlük klasör yapısında üretir. Web UI gerçek zamanlı izleme sunar ve açıldığında ana uygulamayı otomatik başlatır.

## Gereksinimler
- .NET SDK 9.x
- macOS/Linux/Windows

## Hızlı Başlangıç
1) Derle
```bash
dotnet restore
dotnet build -c Debug
```
2) Web UI’yi çalıştır (MainControllerApp otomatik başlar)
```bash
dotnet run --project FileJobRouterWebUI -c Debug
```
3) Tarayıcı: `http://localhost:5036`

## Dizinyapısı ve Günlük (daily) Şema
- İzlenen klasör: `data/Test`
- İşlenenler (output): `data/Processed/<yyyy-MM-dd>/<app>/...`
- Kuyruk: `queue/<yyyy-MM-dd>/queue.json`
- Loglar:
  - Main: `logs/<user>/<yyyy-MM-dd>/app.log`
  - Web: `logs/<user>/<yyyy-MM-dd>/web.log`
- Job kayıtları: `jobs/<user>/<yyyy-MM-dd>/*.json`

## Çalışma Mantığı
- Uygulama açılınca `FileSystemWatcher` aktif olur ve mevcut dosyaları da işler.
- Klasör altı `abc`, `xyz`, `signer` gibi alt dizinlerle `Mappings` eşleşir.
- Kök dizine düşen dosyalar için ilk aşamada `user_choice` job’ı oluşturulur ve kullanıcı seçimi gerekirse CLI’dan istenir.
- Daha önce `Completed` olan dosyalar dahi tekrar işlenebilir. `Pending/Processing` olanlar skip edilir.
- Ctrl+C ile durduğunda çalışan tüm worker prosesleri kill edilir, mutex serbest kalır.

## Worker Çalıştırma Stratejisi
- Öncelik yerel apphost:
  - Windows: `WorkerAppXYZ.exe`
  - macOS/Linux: uzantısız apphost (`WorkerAppXYZ`)
- Bulunamazsa fallback: `dotnet WorkerAppXYZ.dll`

Self-contained apphost üretmek istersen:
```bash
# macOS örnekleri
dotnet publish apps/WorkerAppABC   -c Release -r osx-x64 --self-contained true
dotnet publish apps/WorkerAppXYZ   -c Release -r osx-x64 --self-contained true
dotnet publish apps/WorkerAppSigner -c Release -r osx-x64 --self-contained true
# Windows örnekleri
dotnet publish apps/WorkerAppABC   -c Release -r win-x64 --self-contained true
```

## Konfigürasyon (`config.json`)
```json
{
  "WatchDirectory": "data/Test",
  "TimeoutSeconds": 30,
  "LogDirectory": "logs",
  "JobsDirectory": "jobs",
  "QueueBaseDirectory": "queue",
  "MutexName": "Global\\FileJobRouterDeviceMutex",
  "Mappings": {
    "abc":   { "ExecutablePath": "apps/WorkerAppABC/bin/Debug/net9.0/WorkerAppABC",   "OutputDirectory": "data/Processed/abc" },
    "xyz":   { "ExecutablePath": "apps/WorkerAppXYZ/bin/Debug/net9.0/WorkerAppXYZ",   "OutputDirectory": "data/Processed/xyz" },
    "signer":{ "ExecutablePath": "apps/WorkerAppSigner/bin/Debug/net9.0/WorkerAppSigner","OutputDirectory": "data/Processed/signer" }
  }
}
```
- `QueueFilePath` kaldırıldı. Sistem `QueueBaseDirectory` + gün ile `queue/<yyyy-MM-dd>/queue.json` üretir.
- `OutputDirectory`’ye gün klasörü runtime’da eklenir.

## Web UI
- SignalR ile canlı log ve job güncellemeleri.
- Sistem durdur/başlat butonları kaldırıldı; sistem otomatik çalışır.
- Varsayılan rota: `Dashboard/Index`.
- Web UI açılınca `MainControllerApp` otomatik başlar.
- Ana uygulamanın Web UI hub URL’si otomatik denenir. Elle vermek için çevresel değişken:
  - `FILEJOBROUTER_WEBUI_URL=https://localhost:7155` (veya `http://localhost:5036`)

## Temiz Test (sıfırdan)
```bash
# Çalışanları durdur
pkill -f MainControllerApp || true
pkill -f FileJobRouterWebUI || true
# Klasörleri temizle
rm -rf logs jobs queue data/Processed queue.json
# Stale mutex temizle (macOS örneği)
rm -f "$(getconf DARWIN_USER_TEMP_DIR)"FileJobRouter_FileJobRouterDeviceMutex.lock || true
# Web UI çalıştır
dotnet run --project FileJobRouterWebUI -c Debug
```

## Sorun Giderme
- 403/bağlantı sorunları: Web UI portları `http://localhost:5036` ve `https://localhost:7155`. Ana uygulama bu adresleri otomatik dener. Gerekirse `FILEJOBROUTER_WEBUI_URL` ayarla.
- Çifte assembly (.dll/.exe) çatışmaları: Bin/obj temizleyip yeniden derle.
- `.DS_Store` gibi dosyalar izlemeye takılabilir; istersen watch klasöründe ignore uygula.

## Lisans
Bu depo iç kullanım içindir.

## Cross-platform notes

- Global lock directory
  - Default: Windows -> %PROGRAMDATA%/FileJobRouter/locks, macOS/Linux -> /tmp/FileJobRouter
  - Override: set environment variable `FILEJOBROUTER_LOCK_DIR`
- Executable path tokens
  - Supports `{username}`, `{day}`, and OS environment variables in `config.json` `ExecutablePath` values
- Health
  - Main sends SignalR heartbeat every 5s; WebUI infers Running/Disconnected via heartbeat with 15s threshold
- Shutdown
  - Ctrl+C/SIGTERM: Main performs graceful shutdown, cancels processing loop, kills child workers, releases mutex, removes PID file
- Queue rotation
  - Queue path computed dynamically per operation for the current day; atomic writes with tmp+replace
- Concurrency guarantee
  - Only one Processing at any time across users/instances
- Retry
  - WebUI dispatches retry via hub; queue writes are single-writer (Main)
