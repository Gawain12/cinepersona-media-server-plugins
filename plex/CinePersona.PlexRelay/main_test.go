package main

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

func TestReadPlexPayloadFromJSON(t *testing.T) {
	request := httptest.NewRequest("POST", "/webhook", strings.NewReader(`{"event":"media.scrobble"}`))
	payload, err := readPlexPayload(request)
	if err != nil {
		t.Fatalf("readPlexPayload returned error: %v", err)
	}
	if string(payload) != `{"event":"media.scrobble"}` {
		t.Fatalf("unexpected payload: %s", payload)
	}
}

func TestHandleWebhookForwardsMovieScrobble(t *testing.T) {
	var forwarded plexPayload
	var forwardedKey string

	upstream := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		forwardedKey = r.Header.Get("X-API-Key")
		if err := r.ParseMultipartForm(1 << 20); err != nil {
			t.Errorf("upstream received invalid multipart form: %v", err)
			w.WriteHeader(http.StatusBadRequest)
			return
		}
		if err := json.Unmarshal([]byte(r.FormValue("payload")), &forwarded); err != nil {
			t.Errorf("upstream received invalid payload: %v", err)
			w.WriteHeader(http.StatusBadRequest)
			return
		}
		w.WriteHeader(http.StatusAccepted)
	}))
	defer upstream.Close()

	cfg := config{
		CinePersonaURL: upstream.URL,
		CinePersonaKey: "cpk_test",
		WebhookSecret:  "secret",
		RequestTimeout: time.Second,
	}
	request := httptest.NewRequest(http.MethodPost, "/webhook?secret=secret", strings.NewReader(`{
  "event":"media.scrobble",
  "Metadata":{"type":"movie","title":"Inception","year":2010}
}`))
	request.Header.Set("Content-Type", "application/json")
	response := httptest.NewRecorder()

	handleWebhook(response, request, cfg)

	if response.Code != http.StatusAccepted {
		t.Fatalf("expected accepted response, got %d: %s", response.Code, response.Body.String())
	}
	if forwardedKey != "cpk_test" {
		t.Fatalf("unexpected forwarded API key: %q", forwardedKey)
	}
	if forwarded.Event != "media.scrobble" || forwarded.Metadata.Title != "Inception" {
		t.Fatalf("unexpected forwarded payload: %#v", forwarded)
	}
}
