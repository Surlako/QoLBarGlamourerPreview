# GitHub setup

1. Create a new **public** GitHub repository named `QoLBarGlamourPreview`.
2. Do not initialize it with a README, license, or `.gitignore`.
3. Upload every file and folder from this directory to the root of the repository. Keep `.github/workflows/build-release.yml` in that exact path.
4. Open the repository's **Actions** tab.
5. Select **Build and publish plugin**, choose **Run workflow**, and run it on `main`.
6. When the workflow succeeds, it creates:
   - a GitHub release containing `latest.zip`;
   - `pluginmaster.json` in the repository root.
7. Add this URL in Dalamud under `/xlsettings` → **Experimental** → **Custom Plugin Repositories**:

   `https://raw.githubusercontent.com/YOUR_GITHUB_USERNAME/QoLBarGlamourPreview/main/pluginmaster.json`

Replace `YOUR_GITHUB_USERNAME` with your GitHub username.

## Publishing a later update

1. Increase `<Version>` in `QoLBarGlamourPreview.csproj`.
2. Commit the source changes.
3. Run **Build and publish plugin** again.

Each version number may only be released once.
