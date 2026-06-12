FROM node:22-alpine AS build
WORKDIR /frontend

ARG VITE_CONTRACT_ACCOUNT=swap.neo
ARG VITE_API_BASE_URL=/api
ARG VITE_ALCOR_ENDPOINT=https://wax.alcor.exchange
ARG VITE_HUB_URL=/hub
ENV VITE_CONTRACT_ACCOUNT=$VITE_CONTRACT_ACCOUNT
ENV VITE_API_BASE_URL=$VITE_API_BASE_URL
ENV VITE_ALCOR_ENDPOINT=$VITE_ALCOR_ENDPOINT
ENV VITE_HUB_URL=$VITE_HUB_URL

COPY frontend/package*.json ./
RUN npm ci

COPY frontend/ ./
RUN npm run build

FROM node:22-alpine AS runtime
WORKDIR /app
RUN npm install --global serve@14.2.4
COPY --from=build /frontend/dist ./dist
EXPOSE 3000
CMD ["serve", "-s", "dist", "-l", "tcp://0.0.0.0:3000"]