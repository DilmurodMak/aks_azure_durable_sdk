FROM python:3.13-slim AS build
WORKDIR /app
ARG DEPENDENCY_DIRECTORY=.build/site
COPY ${DEPENDENCY_DIRECTORY}/ /site/
COPY approval/ ./approval/
COPY static/ ./static/

FROM python:3.13-slim
RUN useradd --uid 10001 --create-home approval && mkdir /data && chown approval /data
WORKDIR /app
COPY --from=build /app /app
COPY --from=build /site /usr/local/lib/python3.13/site-packages/
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1
USER approval
CMD ["python", "-m", "approval.worker"]
