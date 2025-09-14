import warnings
warnings.filterwarnings("ignore", category=UserWarning, module="pydantic")

import os
import re
import json
import time
import requests
from github import Github, Auth, GithubException
from google import genai

# =========================
# Settings
# =========================
GITHUB_TOKEN = "YOUR_GITHUB_TOKEN"
GEMINI_API_KEY = "YOUR_GEMINI_API_KEY"
MODEL = "gemini-2.5-flash"
REPO_NAME = "RustPlugins2"
TOPIC = "rustplugin"
PLUGINS_JSON = "plugins.json"

# Source repo for default files
SOURCE_REPO_RAW = "https://raw.githubusercontent.com/muxira/RustPlugins/main"

# =========================
# Initialize clients
# =========================
try:
    g = Github(auth=Auth.Token(GITHUB_TOKEN))
    user = g.get_user()
    user.login  # simple check
except GithubException as e:
    print(f"❌ GitHub API error: {e}")
    exit(1)

try:
    client = genai.Client(api_key=GEMINI_API_KEY)
    client.models.list()  # simple check
except Exception as e:
    print(f"❌ Google Gemini API error: {e}")
    exit(1)

# =========================
# Utilities
# =========================
def sanitize_filename(name: str) -> str:
    return re.sub(r'[\/\\\?\%\*\:\|\"\<\>]', '-', name).strip()

def extract_info(code: str):
    match = re.search(r'\[Info\(".*?",\s*"(.*?)",\s*"(.*?)"\)', code)
    if match:
        return match.groups()
    return "Unknown", "1.0.0"

def generate_description(file_path: str) -> str:
    with open(file_path, "r", encoding="utf-8", errors="ignore") as f:
        plugin_code = f.read()
    author, _ = extract_info(plugin_code)
    prompt = f"Task: Generate one-sentence plugin description for Rust server plugin.\n"
    prompt += f"Format: Rust Server Plugin that <short description>. Original Source: {author}\n\n"
    prompt += plugin_code

    while True:
        try:
            response = client.models.generate_content(model=MODEL, contents=prompt)
            return response.text.strip()
        except Exception as e:
            msg = str(e)
            if "RESOURCE_EXHAUSTED" in msg or "429" in msg:
                print("⚠ Quota limit reached! Waiting 60 seconds...")
                time.sleep(60)
                continue
            raise

def download_file(url, local_path):
    try:
        r = requests.get(url)
        r.raise_for_status()
        with open(local_path, "wb") as f:
            f.write(r.content)
        print(f"✅ Downloaded {local_path}")
        return True
    except Exception as e:
        print(f"❌ Failed to download {url}: {e}")
        return False

# =========================
# Main process
# =========================
def main():
    generate_desc = input("Generate plugin descriptions via Gemini? (y/n): ").lower() == "y"

    # Check or create RustPlugins repository
    try:
        repo = user.get_repo(REPO_NAME)
        print(f"Repository {REPO_NAME} exists")
    except GithubException:
        repo = user.create_repo(REPO_NAME, private=False)
        repo.replace_topics([TOPIC])
        print(f"✅ Created repository {REPO_NAME} with topic '{TOPIC}'")

        # Download README.md and plugin_uploader.py from source repo
        for filename in ["README.md", "plugin_uploader.py"]:
            local_path = filename
            url = f"{SOURCE_REPO_RAW}/{filename}"
            if download_file(url, local_path):
                with open(local_path, "r", encoding="utf-8") as f:
                    content = f.read()
                try:
                    repo.create_file(filename, f"Add {filename}", content)
                    print(f"✅ Uploaded {filename} to {REPO_NAME}")
                except GithubException as e:
                    print(f"❌ Failed to upload {filename}: {e}")

    # Check or create README.md if missing
    try:
        repo.get_contents("README.md")
    except GithubException:
        default_readme = "# RustPlugins\nDefault README created."
        repo.create_file("README.md", "Add main README", default_readme)
        print("✅ README.md created in repository")

    # Load existing plugins.json
    if os.path.exists(PLUGINS_JSON):
        with open(PLUGINS_JSON, "r", encoding="utf-8") as f:
            plugins = json.load(f)
    else:
        plugins = {}

    cs_files = [f for f in os.listdir(".") if f.endswith(".cs")]

    for file in cs_files:
        plugin_name = os.path.splitext(file)[0]
        key = plugin_name

        if key in plugins and plugins[key].get("uploaded"):
            print(f"⚠ Plugin {plugin_name} already uploaded, skipping")
            continue

        print(f"▶ Processing {file} ...")
        with open(file, "r", encoding="utf-8") as f:
            code = f.read()

        author, version = extract_info(code)
        folder_name = sanitize_filename(f"[{plugin_name} - {author} - {version}]")
        path_in_repo = f"{folder_name}/{file}"

        description = ""
        if generate_desc:
            try:
                description = generate_description(file)
            except Exception as e:
                print(f"⚠ Error generating description for {plugin_name}: {e}")

        # Upload or update .cs file
        try:
            contents = repo.get_contents(path_in_repo)
            repo.update_file(path_in_repo, f"Update {folder_name}", code, contents.sha)
            print(f"✅ Updated {path_in_repo}")
        except GithubException:
            repo.create_file(path_in_repo, f"Upload {folder_name}", code)
            print(f"✅ Created {path_in_repo}")

        # Upload README.md for plugin if description exists
        if description:
            readme_path = f"{folder_name}/README.md"
            try:
                contents = repo.get_contents(readme_path)
                repo.update_file(readme_path, f"Update README for {folder_name}", description, contents.sha)
            except GithubException:
                repo.create_file(readme_path, f"Add README for {folder_name}", description)
            print(f"✅ README.md for {folder_name} created/updated")

        # Update plugins.json
        plugins[key] = {
            "pluginName": plugin_name,
            "author": author,
            "version": version,
            "description": description,
            "folder": folder_name,
            "uploaded": True
        }

        with open(PLUGINS_JSON, "w", encoding="utf-8") as f:
            json.dump(plugins, f, indent=2, ensure_ascii=False)

    # Save repository links
    repo_links = [repo.html_url]
    with open("repos.json", "w", encoding="utf-8") as f:
        json.dump(repo_links, f, indent=2)

    print("✅ All plugins uploaded, links saved in repos.json")


if __name__ == "__main__":
    main()
