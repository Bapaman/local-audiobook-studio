# Проверка исходников

Проверки разделены на обычную логику приложения и работу с настоящими речевыми моделями. Для команд ниже нужны Windows, закреплённый .NET SDK, Node.js и Python 3.11. Модели и личные записи не требуются.

Из корня репозитория:

```powershell
dotnet restore LocalAudiobookStudio.sln
dotnet test --project tests/LocalAudiobookStudio.Core.Tests/LocalAudiobookStudio.Core.Tests.csproj -c Release --no-restore
dotnet test --project tests/LocalAudiobookStudio.Application.Tests/LocalAudiobookStudio.Application.Tests.csproj -c Release --no-restore
dotnet test --project tests/LocalAudiobookStudio.Web.Tests/LocalAudiobookStudio.Web.Tests.csproj -c Release --no-restore
```

Клиент:

```powershell
cd src/LocalAudiobookStudio.App/ClientApp
npm ci
npm test
npm run build
```

Python-обработчики, снова из корня:

```powershell
python -I -S -B -m unittest discover -s tests/VoxCpmInferenceWorker.Tests -v
python -I -S -B -m unittest discover -s tests/VoxCpmTrainingWorker.Tests -v
```

Эти Python-проверки используют искусственные данные и подмены границ библиотек. Они не загружают речевую модель и не оценивают похожесть голоса. Маленькие `.b64`-файлы в `tests/fixtures/` — синтетические тестовые данные, не личные сохранения обучения.

В GitHub Actions есть ручной workflow «Проверка исходников». Он проверяет код без запуска обучения и без скачивания весов. Его успешный запуск не заменяет проверку приложения с настоящим движком и прослушивание результата.
