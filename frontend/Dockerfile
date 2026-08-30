# IoBuild Frontend — Dockerfile (wrapped)
# Build context: ./frontend

FROM node:20-alpine AS build
WORKDIR /app

ARG VITE_CLOUDINARY_CLOUD_NAME
ARG VITE_CLOUDINARY_UPLOAD_PRESET
ARG VITE_API_URL=/api/v1

COPY package*.json ./
RUN npm ci

COPY . .

RUN if [ -n "$VITE_CLOUDINARY_CLOUD_NAME" ]; then \
      echo "VITE_CLOUDINARY_CLOUD_NAME=$VITE_CLOUDINARY_CLOUD_NAME" >> .env.production.local; \
    fi && \
    if [ -n "$VITE_CLOUDINARY_UPLOAD_PRESET" ]; then \
      echo "VITE_CLOUDINARY_UPLOAD_PRESET=$VITE_CLOUDINARY_UPLOAD_PRESET" >> .env.production.local; \
    fi

RUN npm run build

FROM nginx:alpine
RUN apk add --no-cache curl
COPY --from=build /app/dist /usr/share/nginx/html
# SPA fallback; API is proxied by cutover nginx (iobuild-api:8080)
RUN printf 'server {\n  listen 80;\n  root /usr/share/nginx/html;\n  index index.html;\n  location / {\n    try_files $uri $uri/ /index.html;\n  }\n}\n' > /etc/nginx/conf.d/default.conf
EXPOSE 80
