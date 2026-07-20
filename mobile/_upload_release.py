"""Upload Windows zip to GitHub release. Token from env only — never hardcode."""
import os
import sys

try:
    import httpx
except ImportError:
    print("Install httpx: pip install httpx")
    sys.exit(1)

TOKEN = os.environ.get("GITHUB_TOKEN") or os.environ.get("GH_TOKEN")
if not TOKEN:
    print("Set GITHUB_TOKEN (or GH_TOKEN) env var before running.")
    sys.exit(1)

REPO = os.environ.get("GITHUB_REPO", "hevron16-gif/cardiagnostik")
TAG = os.environ.get("RELEASE_TAG", "v1.0.15")
ZIP = os.environ.get(
    "RELEASE_ZIP",
    os.path.join(os.path.dirname(__file__), "CarDiagnosticApp_Windows_v1.0.15_fixed.zip"),
)

if not os.path.exists(ZIP):
    print(f"ERROR: {ZIP} not found")
    sys.exit(1)

headers = {
    "Authorization": f"Bearer {TOKEN}",
    "Accept": "application/vnd.github+json",
    "X-GitHub-Api-Version": "2022-11-28",
}

api = "https://api.github.com"


async def main():
    async with httpx.AsyncClient(timeout=120.0) as client:
        resp = await client.get(f"{api}/repos/{REPO}/releases/tags/{TAG}", headers=headers)
        if resp.status_code != 200:
            print(f"ERROR getting release: {resp.status_code} {resp.text[:300]}")
            sys.exit(1)

        release = resp.json()
        release_id = release["id"]
        print(f"Found release: {release['name']} (id={release_id})")

        for asset in release.get("assets", []):
            if "Windows" in asset["name"] or asset["name"].endswith(".zip"):
                del_resp = await client.delete(asset["url"], headers=headers)
                print(f"Deleted: {asset['name']} ({del_resp.status_code})")

        name = os.path.basename(ZIP)
        upload_url = release["upload_url"].split("{")[0]
        with open(ZIP, "rb") as f:
            data = f.read()
        up = await client.post(
            f"{upload_url}?name={name}",
            headers={**headers, "Content-Type": "application/octet-stream"},
            content=data,
        )
        print(f"Upload: {up.status_code} {up.text[:200]}")


if __name__ == "__main__":
    import asyncio

    asyncio.run(main())
