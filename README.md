# RustPlugins

---

### [🇬🇧 English Version](#english-version) | [🇷🇺 Русская версия](#russian-version)

---

## 🇬🇧 English Version <a id="english-version"></a>

Welcome to **RustPlugins** – a repository containing various plugins for Rust servers, fully indexed on [Plugins Forum Github](https://github.com/publicrust/plugins-forum). To see how many plugins there already are, check out the [Plugins Forum](https://plugins-forum.vercel.app).

### Description

This repository stores various Rust server plugins, fully indexed on [Plugins Forum Github](https://github.com/publicrust/plugins-forum).

### Script Setup

Before using `plugin_uploader.py`, you need to configure it properly:

1. Open `plugin_uploader.py` in a text editor.
2. Set your **GitHub API Token**:
    ```python
    GITHUB_TOKEN = "YOUR_GITHUB_TOKEN"
    ```
3. (Optional) Set your **Google Gemini API Key** to generate neural plugin descriptions:
    ```python
    GEMINI_API_KEY = "YOUR_GOOGLE_GEMINI_API_KEY"
    ```
4. Adjust other settings if needed: `MODEL`, `REPO_NAME`, `TOPIC`.
5. Save the changes. The script is now ready to use.

### How to quickly upload plugins to your GitHub?

1.  Create a GitHub API Key with **repo** permissions (create & edit repositories).  
    * Go to **Settings → Developer Settings → Personal Access Tokens → Tokens (classic) → Generate new token**.  
    * Select permissions: `repo` and `public_repo` (if your repo is public).

2.  (Optional) Create a Google Gemini API Key to generate neural descriptions for plugins.  
    * Limit: 10 plugins per minute.

3.  Install Python 3.9+ and required libraries:
    ```bash
    pip install PyGithub google-genai
    ```

4.  Place `plugin_uploader.py` in the folder with your plugins.

5.  Extract all `.cs` plugin files next to the script.

6.  Run the script from the terminal:
    ```bash
    python plugin_uploader.py
    ```

7.  Choose whether to generate descriptions via neural network.

8.  Enjoy – plugins will be automatically uploaded to GitHub.

---

## 🇷🇺 Русская версия <a id="russian-version"></a>

Добро пожаловать в **RustPlugins** — репозиторий, содержащий различные плагины для серверов Rust, полностью индексированные на [Plugins Forum Github](https://github.com/publicrust/plugins-forum). Чтобы увидеть, сколько плагинов уже есть, загляните на [Plugins Forum](https://plugins-forum.vercel.app).

### Описание

Это репозиторий хранит различные плагины для серверов Rust, полностью индексированные на [Plugins Forum Github](https://github.com/publicrust/plugins-forum).

### Настройка скрипта

Перед использованием `plugin_uploader.py` нужно настроить его:

1. Откройте `plugin_uploader.py` в любом текстовом редакторе.
2. Установите ваш **GitHub API Token**:
    ```python
    GITHUB_TOKEN = "ВАШ_GITHUB_TOKEN"
    ```
3. (Опционально) Установите ваш **Google Gemini API Key** для генерации описаний плагинов нейросетью:
    ```python
    GEMINI_API_KEY = "ВАШ_GOOGLE_GEMINI_API_KEY"
    ```
4. Настройте остальные параметры при необходимости: `MODEL`, `REPO_NAME`, `TOPIC`.
5. Сохраните изменения. Скрипт готов к использованию.

### Как быстро загружать плагины на GitHub?

1.  Создайте GitHub API Key с разрешением **repo** (создание и редактирование репозиториев).  
    * Перейдите в **Settings → Developer Settings → Personal Access Tokens → Tokens (classic) → Generate new token**.  
    * Выберите разрешения: `repo` и `public_repo` (если репозиторий публичный).

2.  (Опционально) Создайте Google Gemini API Key для генерации описаний плагинов нейросетью.  
    * Лимит: 10 плагинов в минуту.

3.  Установите Python 3.9+ и необходимые библиотеки:
    ```bash
    pip install PyGithub google-genai
    ```

4.  Поместите `plugin_uploader.py` в папку с плагинами.

5.  Распакуйте все `.cs` плагины рядом со скриптом.

6.  Запустите скрипт через терминал:
    ```bash
    python plugin_uploader.py
    ```

7.  Выберите, создавать ли описание через нейросеть или нет.

8.  Наслаждайтесь процессом – плагины будут автоматически загружены на GitHub.
ы