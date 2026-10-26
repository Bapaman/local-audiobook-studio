# Внешние компоненты

Проект использует готовые библиотеки и речевые модели. Их авторство и условия использования относятся к соответствующим разработчикам.

| Компонент | Использование | Источник |
|---|---|---|
| VoxCPM / VoxCPM2 | Синтез речи и дообучение LoRA | [OpenBMB/VoxCPM](https://github.com/OpenBMB/VoxCPM), [веса VoxCPM2](https://huggingface.co/openbmb/VoxCPM2) |
| Whisper large-v3-turbo | Расшифровка речи | [OpenAI/Whisper](https://github.com/openai/whisper), [модель](https://huggingface.co/openai/whisper-large-v3-turbo) |
| llama.cpp-omni | Ранний вариант локального синтеза | [tc-mb/llama.cpp-omni](https://github.com/tc-mb/llama.cpp-omni) |
| RVC | Ранний режим преобразования тембра | [RVC Project](https://github.com/RVC-Project/Retrieval-based-Voice-Conversion-WebUI) |
| PyTorch, NumPy, librosa | Вычисления и обработка аудио | [PyTorch](https://pytorch.org/), [NumPy](https://numpy.org/), [librosa](https://librosa.org/) |
| ASP.NET Core, React, Vite, SQLite | Сервер, интерфейс и хранение данных | См. файлы зависимостей проекта |

Закреплённые версии, сведения о лицензиях и контрольные суммы внешних ресурсов находятся в `eng/locks/`. Для VoxCPM2 в lock-файле указана Apache-2.0, для Whisper и llama.cpp-omni — MIT. При подготовке внешних исходников приложение сохраняет их лицензию; патчи к llama.cpp-omni находятся в `eng/patches/`.

Веса моделей, Python-окружения, полный код внешних движков и личные записи не распространяются в этом репозитории. Файлы NuGet/npm/Python-зависимостей описывают состав окружения; их лицензии остаются у соответствующих компонентов.
