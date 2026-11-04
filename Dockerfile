# SQLite holds the conversation log and is created on start. The four demo consignments are seeded in memory by FakeTrackingApi, and the offline model needs no key.
# Build from the repo root: docker build -t harbour-support-agent .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Harbour.SupportAgent/Harbour.SupportAgent.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS run
WORKDIR /app
COPY --from=build /app .
ENV Database__Path=/tmp/data/support.db
ENV PORT=8080
EXPOSE 8080
# Runs as the image's non-root user, so anything written goes under /tmp. Free hosting has no lasting disk anyway.
USER $APP_UID
CMD ["sh", "-c", "exec dotnet Harbour.SupportAgent.dll --urls http://0.0.0.0:$PORT"]
