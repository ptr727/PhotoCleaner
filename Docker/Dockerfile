# Stage 1 - build
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY PhotoCleaner/ PhotoCleaner/

RUN dotnet publish PhotoCleaner/PhotoCleaner.csproj \
        --configuration Release \
        --output /app/publish

# Stage 2 - runtime
FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine AS final

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
