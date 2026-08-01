# PhotoCleaner

An application that prepares photos and videos for import into photo managers.

## Documentation

Refer to the [project page][github] for complete usage and configuration.

- **Source Code**: [GitHub][github] - source code, issues, and CI/CD pipelines.
- **Binary Releases**: [GitHub Releases][releases] - pre-compiled executables for Windows, Linux, and macOS.
- **Docker Images**: [Docker Hub][dockerhub] - container images with exiftool and ffmpeg pre-installed.

## Docker Tags

Images are rebuilt weekly to pick up upstream base-image and tool updates.

- `latest`: built from the release [main branch][main-branch]. Multi-architecture (`linux/amd64`, `linux/arm64`) on the `dotnet/runtime:10.0-alpine` base.
- `develop`: built from the pre-release [develop branch][develop-branch].
- `X.Y.Z`: a specific released version (SemVer2 tag).

## License

Licensed under the [MIT License][license].\
![GitHub License][license-shield]

<!-- Repo -->

[develop-branch]: https://github.com/ptr727/PhotoCleaner/tree/develop
[github]: https://github.com/ptr727/PhotoCleaner
[license]: https://github.com/ptr727/PhotoCleaner/blob/main/LICENSE
[main-branch]: https://github.com/ptr727/PhotoCleaner/tree/main
[releases]: https://github.com/ptr727/PhotoCleaner/releases

<!-- External -->

[dockerhub]: https://hub.docker.com/r/ptr727/photocleaner

<!-- Shields -->

[license-shield]: https://img.shields.io/github/license/ptr727/PhotoCleaner
