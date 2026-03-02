package session

import "time"

func (s *Service) PruneExpired(now time.Time) int {
	s.mu.Lock()
	defer s.mu.Unlock()

	removed := 0
	for id, sess := range s.sessionsByID {
		if now.After(sess.ExpiresAt) || sess.State == StateClosed {
			delete(s.sessionByLogin, sess.LoginCode)
			delete(s.sessionsByID, id)
			removed++
		}
	}
	return removed
}
