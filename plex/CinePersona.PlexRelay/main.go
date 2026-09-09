package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log"
	"mime/multipart"
	"net/http"
	"os"
	"strconv"
	"strings"
	"time"
)

type config struct {
	ListenAddr     string
	CinePersonaURL string
	CinePersonaKey string
	WebhookSecret  string
	RequestTimeout time.Duration
}

type plexPayload struct {
	Event    string       `json:"event"`
	Metadata plexMetadata `json:"Metadata"`
}

type plexMetadata struct {
	Type  string     `json:"type"`
	Title string     `json:"title"`
	Year  int        `json:"year"`
	Guid  []plexGuid `json:"Guid"`
}

type plexGuid struct {
	ID string `json:"id"`
}

func main() {
	cfg := loadConfig()
	if cfg.CinePersonaKey == "" {
		log.Fatal("CINEPERSONA_API_KEY is required")
	}

	server := &http.Server{
		Addr:              cfg.ListenAddr,
		Handler:           routes(cfg),
		ReadHeaderTimeout: 10 * time.Second,
		ReadTimeout:       30 * time.Second,
		WriteTimeout:      30 * time.Second,
		IdleTimeout:       60 * time.Second,
	}
	log.Printf("CinePersona Plex relay listening on %s", cfg.ListenAddr)
	if err := server.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
		log.Fatal(err)
	}
}

func routes(cfg config) http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("/healthz", func(w http.ResponseWriter, _ *http.Request) {
		writeJSON(w, http.StatusOK, map[string]string{"status": "ok"})
	})
	mux.HandleFunc("/webhook", func(w http.ResponseWriter, r *http.Request) {
		handleWebhook(w, r, cfg)
	})
	return mux
}

func handleWebhook(w http.ResponseWriter, r *http.Request, cfg config) {
	if r.Method != http.MethodPost {
		writeJSON(w, http.StatusMethodNotAllowed, map[string]string{"error": "method not allowed"})
		return
	}
	if cfg.WebhookSecret != "" && r.URL.Query().Get("secret") != cfg.WebhookSecret {
		writeJSON(w, http.StatusUnauthorized, map[string]string{"error": "invalid webhook secret"})
		return
	}

	payload, err := readPlexPayload(r)
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]string{"error": err.Error()})
		return
	}

	var event plexPayload
	if err := json.Unmarshal(payload, &event); err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]string{"error": "invalid Plex payload"})
		return
	}
	if !strings.EqualFold(event.Event, "media.scrobble") || !strings.EqualFold(event.Metadata.Type, "movie") {
		writeJSON(w, http.StatusOK, map[string]string{"status": "ignored"})
		return
	}

	if err := forwardToCinePersona(r.Context(), cfg, payload); err != nil {
		log.Printf("forwarding Plex scrobble failed: %v", err)
		writeJSON(w, http.StatusBadGateway, map[string]string{"error": "CinePersona request failed"})
		return
	}

	writeJSON(w, http.StatusAccepted, map[string]string{
		"status": "accepted",
		"title":  event.Metadata.Title,
	})
}

func readPlexPayload(r *http.Request) ([]byte, error) {
	contentType := r.Header.Get("Content-Type")
	if strings.HasPrefix(strings.ToLower(contentType), "multipart/") {
		if err := r.ParseMultipartForm(4 << 20); err != nil {
			return nil, fmt.Errorf("invalid multipart payload: %w", err)
		}
		value := strings.TrimSpace(r.FormValue("payload"))
		if value == "" {
			return nil, errors.New("missing Plex payload field")
		}
		return []byte(value), nil
	}

	body, err := io.ReadAll(io.LimitReader(r.Body, 4<<20))
	if err != nil {
		return nil, err
	}
	if len(bytes.TrimSpace(body)) == 0 {
		return nil, errors.New("empty Plex payload")
	}
	return body, nil
}

func forwardToCinePersona(ctx context.Context, cfg config, payload []byte) error {
	var body bytes.Buffer
	writer := multipart.NewWriter(&body)
	part, err := writer.CreateFormField("payload")
	if err != nil {
		return err
	}
	if _, err := part.Write(payload); err != nil {
		return err
	}
	if err := writer.Close(); err != nil {
		return err
	}

	endpoint := strings.TrimRight(cfg.CinePersonaURL, "/") + "/api/v1/webhook/plex"
	request, err := http.NewRequestWithContext(ctx, http.MethodPost, endpoint, &body)
	if err != nil {
		return err
	}
	request.Header.Set("Content-Type", writer.FormDataContentType())
	request.Header.Set("X-API-Key", cfg.CinePersonaKey)

	client := &http.Client{Timeout: cfg.RequestTimeout}
	response, err := client.Do(request)
	if err != nil {
		return err
	}
	defer response.Body.Close()
	if response.StatusCode < http.StatusOK || response.StatusCode >= http.StatusMultipleChoices {
		responseBody, _ := io.ReadAll(io.LimitReader(response.Body, 4<<10))
		return fmt.Errorf("CinePersona returned %s: %s", response.Status, strings.TrimSpace(string(responseBody)))
	}
	return nil
}

func loadConfig() config {
	return config{
		ListenAddr:     envOrDefault("PLEX_RELAY_ADDR", ":8080"),
		CinePersonaURL: envOrDefault("CINEPERSONA_URL", "https://cinepersona.com"),
		CinePersonaKey: strings.TrimSpace(os.Getenv("CINEPERSONA_API_KEY")),
		WebhookSecret:  strings.TrimSpace(os.Getenv("PLEX_WEBHOOK_SECRET")),
		RequestTimeout: time.Duration(envIntOrDefault("CINEPERSONA_TIMEOUT_SECONDS", 15)) * time.Second,
	}
}

func envOrDefault(name string, fallback string) string {
	if value := strings.TrimSpace(os.Getenv(name)); value != "" {
		return value
	}
	return fallback
}

func envIntOrDefault(name string, fallback int) int {
	value, err := strconv.Atoi(strings.TrimSpace(os.Getenv(name)))
	if err != nil || value <= 0 {
		return fallback
	}
	return value
}

func writeJSON(w http.ResponseWriter, status int, value any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(value)
}
