FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
ARG DUMPLINGS_COMMIT=unknown
RUN dotnet publish Dumplings.Cli -c Release -o /out -v q \
    && printf '%s\n' "$DUMPLINGS_COMMIT" > /out/COMMIT

FROM mcr.microsoft.com/dotnet/runtime:8.0
COPY --from=build /out /app
RUN mkdir -m 1777 /data
WORKDIR /data
ENTRYPOINT ["dotnet", "/app/Dumplings.Cli.dll"]
