# Проверка исходников

Проверки разделены на обычную логику приложения и работу с настоящими речевыми моделями. Для команд ниже нужны Windows, закреплённый .NET SDK, Node.js и Python 3.11. Модели и личные записи не требуются.

Из корня репозитория:

```powershell
dotnet restore LocalAudiobookStudio.sln
dotnet test --project tests/LocalAudiobookStudio.Core.Tests/LocalAudiobookStudio.Core.Tests.csproj -c Release --no-restore
dotnet test --project tests/LocalAudiobookStudio.Application.Tests/LocalAudiobookStudio.Application.Tests.csproj -c Release --no-restore
dotnet test --project tests/LocalAudiobookStudio.Web.Tests/LocalAudiobookStudio.Web.Tests.csproj -c Release --no-restore
```

Для полного набора .NET-проверок используйте
`dotnet test --solution LocalAudiobookStudio.sln --no-restore --max-parallel-test-modules 1`.
Он включает проверку запуска, повторного открытия и выхода из Windows-приложения
с временным каталогом проектов.

При тайм-ауте в проверке очереди сохраните вывод полного запуска и отдельно
повторите `JobCoordinatorTests` с диагностикой. Оба запуска используют
синтетические задания и временные каталоги проектов.

```powershell
$diagnostics = Join-Path $env:TEMP ("las-queue-" + [guid]::NewGuid().ToString("N"))
dotnet test --project tests/LocalAudiobookStudio.Web.Tests/LocalAudiobookStudio.Web.Tests.csproj `
  --no-restore --diagnostic --diagnostic-output-directory $diagnostics `
  -- --filter-class '*JobCoordinatorTests'
```

В тестах WorkerHost временный отказ Windows при удалении копии
`voxcpm2-cli.exe` обрабатывается в очистке тестовых файлов. Проверки остановки
корневого и дочерних процессов выполняются до очистки. Если блокировка
сохраняется, тест сообщает об ошибке.

Клиент:

```powershell
cd src/LocalAudiobookStudio.App/ClientApp
npm ci
npm test
npm run build
```

Для браузерной проверки соберите тестовый сервер из корня репозитория
и запустите сценарии из папки клиента. Если Chromium для Playwright
ещё не установлен, выполните `npx playwright install chromium` из папки клиента.

```powershell
dotnet build tests/LocalAudiobookStudio.BrowserHost/LocalAudiobookStudio.BrowserHost.csproj --no-restore
cd src/LocalAudiobookStudio.App/ClientApp
npm run test:e2e
```

Сценарии Chromium используют собранный интерфейс и синтетический WAV.
Они проверяют очередь, навигацию, сохранение замечаний, повторное
озвучивание и экспорт.

Python-обработчики, снова из корня:

```powershell
python -I -S -B -m unittest discover -s tests/VoxCpmInferenceWorker.Tests -v
python -I -S -B -m unittest discover -s tests/VoxCpmTrainingWorker.Tests -v
python -I -S -B -m unittest discover -s tests/TtsCorpusWorker.Tests -v
```

Эти Python-проверки используют искусственные данные и подмены границ библиотек. Они не загружают речевую модель и не оценивают похожесть голоса. Маленькие `.b64`-файлы в `tests/fixtures/` — синтетические тестовые данные, не личные сохранения обучения.

В GitHub Actions есть ручной workflow «Проверка исходников». Он проверяет код без запуска обучения и без скачивания весов. Его успешный запуск не заменяет проверку приложения с настоящим движком и прослушивание результата.

## Проверка с голосовым сообщением

1. Запустите Windows-сборку с FFmpeg, средой распознавания и средой VoxCPM2. Параметры запуска есть в [инструкции по установке](SETUP.md).
2. Создайте отдельный тестовый голос. Загрузите OGG с 45–60 секундами речи и проверьте, что длительность и воспроизведение сохранённой записи соответствуют файлу.
3. Нажмите «Перейти к дообучению», подготовьте и подтвердите клипы. Запустите проход. Проверьте появление сохранения и возможность продолжить работу после перезапуска студии.

Для этой проверки подходит синтетическая русская речь. Чтобы оценить сходство, интонацию и ударения, нужна отдельная проба с разрешённой записью человека и её прослушивание.
