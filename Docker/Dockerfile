# Multi-arch build. Build on the native BUILDPLATFORM and cross-publish to TARGETARCH so no
# QEMU emulation is needed for the compile. Only the final runtime layer is per-arch.
#
#   docker buildx build --platform linux/amd64,linux/arm64 --file ./Docker/Dockerfile .
#   docker buildx build --load --platform linux/amd64 --tag photocleaner:local --file ./Docker/Dockerfile .

# Stage 1 - build
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
ARG TARGETARCH
ARG BUILD_CONFIGURATION="Release"
ARG BUILD_VERSION="1.0.0.0"
ARG BUILD_FILE_VERSION="1.0.0.0"
ARG BUILD_ASSEMBLY_VERSION="1.0.0.0"
ARG BUILD_INFORMATION_VERSION="1.0.0.0"
ARG BUILD_PACKAGE_VERSION="1.0.0"
WORKDIR /src

# Central build and package configuration must be present for the versionless PackageReference
# items (ManagePackageVersionsCentrally) to resolve during restore.
COPY Directory.Build.props Directory.Packages.props .editorconfig ./
COPY PhotoCleaner/ PhotoCleaner/

RUN dotnet publish PhotoCleaner/PhotoCleaner.csproj \
        --arch "$TARGETARCH" \
        --configuration "$BUILD_CONFIGURATION" \
        --output /app/publish \
        -property:Version="$BUILD_VERSION" \
        -property:FileVersion="$BUILD_FILE_VERSION" \
        -property:AssemblyVersion="$BUILD_ASSEMBLY_VERSION" \
        -property:InformationalVersion="$BUILD_INFORMATION_VERSION" \
        -property:PackageVersion="$BUILD_PACKAGE_VERSION"

# Stage 2 - runtime
FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine AS final
ARG LABEL_VERSION="1.0.0.0"
LABEL name="PhotoCleaner" \
      version="${LABEL_VERSION}" \
      description="An application that prepares photos and videos for import into photo managers." \
      maintainer="ptr727"

# exiftool: EXIF metadata reader/writer (pulls in Perl)
# ffmpeg:   video conversion; ffprobe ships inside this same package on Alpine
RUN apk add --no-cache \
        exiftool \
        ffmpeg

WORKDIR /app
COPY --from=build /app/publish ./

# Exec form so SIGTERM reaches the .NET process directly.
# CMD ["--help"] gives a useful default when the container is run with no arguments.
ENTRYPOINT ["./PhotoCleaner"]
CMD ["--help"]
